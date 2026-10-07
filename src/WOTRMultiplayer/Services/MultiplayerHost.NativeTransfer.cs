using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerHost
    {
        private sealed class NativeTransfer
        {
            public NetworkGame Session;
            public NetworkCombat Combat;
            public NetworkRealTimeCombatState State;
            public CombatSynchronizationPhase Phase;
            public Func<bool> BarrierOwned;
            public long Peer;
            public NotifyRealTimeNativeCheckpoint Prepare;
            public NotifyRealTimeNativeCheckpoint Manifest;
            public RealTimeNativeCheckpointSender Sender;
            public DateTime Deadline;
            public DateTime NextHeader;
            public bool LoadRequested;
            public readonly TaskCompletionSource<bool> Prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Transferred = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Enclosing reconstruction barrier must subsequently restore/verify its
        // reference state and explicitly Commit or Abort. This receipt never
        // changes the combat phase, clears graph obligations or resumes play.
        internal async Task<NotifyRealTimeNativeCheckpoint> ReconstructCurrentNativeCheckpointAsync(Func<bool> barrierOwned)
        {
            var manifest = await TransferCurrentNativeCheckpointAsync(barrierOwned);
            if (manifest == null) return null;
            var requested = new TaskCompletionSource<NativeTransfer>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainThreadAccessor.Post(() =>
            {
                lock (ActionLock)
                {
                    var transfer = _nativeTransfer;
                    if (!IsCurrentNativeTransfer(transfer) || transfer.Manifest == null
                        || !RealTimeNativeCheckpointLimits.SameIdentity(manifest, transfer.Manifest)
                        || transfer.Manifest.ArchiveHash != manifest.ArchiveHash
                        || !transfer.Transferred.Task.IsCompleted || !transfer.Transferred.Task.Result)
                    { requested.TrySetResult(null); return; }
                    transfer.LoadRequested = true;
                    transfer.Deadline = DateTime.UtcNow.AddSeconds(150);
                    transfer.NextHeader = default;
                    requested.TrySetResult(transfer);
                    TickNativeCheckpointTransfer();
                }
            });
            var owner = await requested.Task;
            if (owner == null || !await owner.Loaded.Task) return null;
            lock (ActionLock)
                return IsCurrentNativeTransfer(owner) ? RealTimeNativeCheckpointLimits.Copy(owner.Manifest) : null;
        }
        private NativeTransfer _nativeTransfer;

        private bool IsCurrentNativeTransfer(NativeTransfer transfer)
        {
            var session = Game;
            return transfer != null
            && ReferenceEquals(_nativeTransfer, transfer) && RealTimePresentationRuntime.Enabled && IsActive
            && ReferenceEquals(session, transfer.Session) && ReferenceEquals(session?.Combat, transfer.Combat)
            && ReferenceEquals(session?.RealTimeCombat, transfer.State) && session.Id == transfer.Prepare.SessionId
            && session.LoadedSaveSeed == transfer.Prepare.LoadedSaveSeed && transfer.State.Epoch == transfer.Prepare.Epoch
            && transfer.State.LastAuthoritativeSequence == transfer.Prepare.LastIncludedSequence
            && transfer.State.Phase == transfer.Phase && transfer.Phase != CombatSynchronizationPhase.Stable
            && transfer.State.Mode == CombatMode.RealTimeWithPause && transfer.BarrierOwned();
        }

        // Future native reconstruction coordinator calls this AFTER owning a
        // repair barrier. Success only means client-verified archive bytes, not
        // loaded mechanics, Commit, resumed combat or permission to save.
        internal async Task<NotifyRealTimeNativeCheckpoint> TransferCurrentNativeCheckpointAsync(Func<bool> barrierOwned)
        {
            if (barrierOwned == null) throw new ArgumentNullException(nameof(barrierOwned));
            NativeTransfer transfer = null;
            var started = new TaskCompletionSource<NativeTransfer>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainThreadAccessor.Post(() =>
            {
                lock (ActionLock)
                {
                    try
                    {
                        if (_nativeTransfer != null || !RealTimePresentationRuntime.Enabled || !IsActive || !barrierOwned()
                            || Game?.Combat == null || Game.RealTimeCombat.Mode != CombatMode.RealTimeWithPause
                            || Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Stable || !GameInteraction.IsPaused)
                            throw new InvalidOperationException("native-transfer-host-barrier-not-ready");
                        var peers = GetSyncedPlayers().Where(peer => peer.Id != Game.LocalPlayerId).ToArray();
                        if (peers.Length != 1) throw new InvalidOperationException("native-transfer-requires-one-client");
                        var fingerprint = Game.Players.FirstOrDefault(player => player.Id == Game.LocalPlayerId)?.ContentState?.RealTimeBuildFingerprint;
                        if (!RealTimeNativeCheckpointLimits.IsHash(fingerprint)
                            || peers[0].ContentState?.RealTimeBuildFingerprint != fingerprint)
                            throw new InvalidOperationException("native-transfer-build-fingerprint-mismatch");
                        var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = CombatInteraction.GetRealTimeCombatState() });
                        var owner = new NativeTransfer
                        {
                            Session = Game,
                            State = Game.RealTimeCombat,
                            Combat = Game.Combat,
                            BarrierOwned = barrierOwned,
                            Phase = Game.RealTimeCombat.Phase,
                            Peer = peers[0].Id,
                            Deadline = DateTime.UtcNow.AddSeconds(60),
                            Prepare = new NotifyRealTimeNativeCheckpoint
                            {
                                SessionId = Game.Id,
                                LoadedSaveSeed = Game.LoadedSaveSeed,
                                Epoch = Game.RealTimeCombat.Epoch,
                                LastIncludedSequence = Game.RealTimeCombat.LastAuthoritativeSequence,
                                CheckpointId = Guid.NewGuid().ToString("N"),
                                BuildFingerprint = fingerprint,
                                CanonicalStateHash = hash,
                                Stage = (int)RealTimeNativeCheckpointStage.Prepare
                            }
                        };
                        if (!RealTimeNativeCheckpointLimits.IsPrepare(owner.Prepare))
                            throw new InvalidOperationException("native-transfer-invalid-barrier-identity");
                        _nativeTransfer = owner;
                        started.TrySetResult(owner);
                        TickNativeCheckpointTransfer();
                    }
                    catch (Exception error) { started.TrySetException(error); }
                }
            });
            try
            {
                transfer = await started.Task;
                if (!await transfer.Prepared.Task) return null;
                bool Authorized() { lock (ActionLock) return IsCurrentNativeTransfer(transfer); }
                using var source = await CaptureCurrentNativeCheckpointAsync(transfer.Prepare.CheckpointId, Authorized);
                var archive = await Task.Run(() => RealTimeNativeCheckpointCodec.Encode(source));
                var installed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _mainThreadAccessor.Post(() =>
                {
                    lock (ActionLock)
                    {
                        try
                        {
                            if (!IsCurrentNativeTransfer(transfer)) { installed.TrySetResult(false); return; }
                            var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = CombatInteraction.GetRealTimeCombatState() });
                            if (hash != transfer.Prepare.CanonicalStateHash)
                                throw new InvalidOperationException("native-transfer-host-changed-after-prepare");
                            var manifest = RealTimeNativeCheckpointLimits.Copy(transfer.Prepare);
                            manifest.Stage = (int)RealTimeNativeCheckpointStage.Manifest;
                            manifest.ArchiveBytes = archive.Length; manifest.ArchiveHash = RealTimeNativeCheckpointLimits.Hash(archive);
                            manifest.ChunkBytes = RealTimeNativeCheckpointLimits.ChunkBytes;
                            manifest.ChunkCount = (archive.Length + manifest.ChunkBytes - 1) / manifest.ChunkBytes;
                            transfer.Sender = new RealTimeNativeCheckpointSender(transfer.Peer, manifest, archive);
                            transfer.Manifest = manifest; transfer.NextHeader = default;
                            transfer.Deadline = DateTime.UtcNow + RealTimeNativeCheckpointLimits.Deadline(archive.Length);
                            TickNativeCheckpointTransfer(); installed.TrySetResult(true);
                        }
                        catch (Exception error) { installed.TrySetException(error); }
                    }
                });
                if (!await installed.Task || !await transfer.Transferred.Task) return null;
                return RealTimeNativeCheckpointLimits.Copy(transfer.Manifest);
            }
            catch (Exception error)
            {
                Logger.LogError(error, "Native checkpoint transfer failed before mechanical reconstruction.");
                _mainThreadAccessor.Post(() => { lock (ActionLock) FailNativeCheckpointTransfer(transfer, "native-transfer-capture-or-send-failed"); });
                return null;
            }
        }

        private void TickNativeCheckpointTransfer()
        {
            lock (ActionLock)
            {
                var transfer = _nativeTransfer;
                if (transfer == null) return;
                if (!IsCurrentNativeTransfer(transfer)) { ResetNativeCheckpointTransfer(); return; }
                if (DateTime.UtcNow >= transfer.Deadline)
                { FailNativeCheckpointTransfer(transfer, "native-transfer-timeout"); return; }
                if (transfer.Loaded.Task.IsCompleted && transfer.Loaded.Task.Result) return;
                try
                {
                    if (DateTime.UtcNow >= transfer.NextHeader)
                    {
                        transfer.NextHeader = DateTime.UtcNow.AddSeconds(1);
                        var header = RealTimeNativeCheckpointLimits.Copy(transfer.Manifest ?? transfer.Prepare);
                        if (transfer.LoadRequested) header.Stage = (int)RealTimeNativeCheckpointStage.Load;
                        Send(transfer.Peer, header);
                    }
                    if (transfer.Sender != null)
                        foreach (var chunk in transfer.Sender.GetDueChunks(DateTime.UtcNow)) Send(transfer.Peer, chunk);
                }
                catch (Exception error)
                {
                    Logger.LogError(error, "Native checkpoint transfer exhausted its bounded send window.");
                    FailNativeCheckpointTransfer(transfer, "native-transfer-send-window-failed");
                }
            }
        }

        private void OnClientNativeCheckpointAck(long receivedFrom, ClientRealTimeNativeCheckpointAck ack)
        {
            if (ack == null || ack.Reason?.Length > 160 || string.IsNullOrWhiteSpace(ack.SessionId)
                || ack.SessionId.Length > 128 || !Guid.TryParseExact(ack.CheckpointId, "N", out _)
                || ack.ArchiveHash != null && !RealTimeNativeCheckpointLimits.IsHash(ack.ArchiveHash)
                || !Enum.IsDefined(typeof(RealTimeNativeCheckpointAckStage), ack.Stage)) return;
            var copied = ProtoBuf.Serializer.DeepClone(ack);
            _mainThreadAccessor.Post(() =>
            {
                lock (ActionLock)
                {
                    var transfer = _nativeTransfer;
                    if (!IsCurrentNativeTransfer(transfer) || receivedFrom != transfer.Peer) return;
                    var expected = transfer.Manifest ?? transfer.Prepare;
                    if (!RealTimeNativeCheckpointLimits.Matches(expected, copied)) return;
                    if (copied.Stage == (int)RealTimeNativeCheckpointAckStage.Failed)
                    { FailNativeCheckpointTransfer(transfer, "native-transfer-client-refused"); return; }
                    if (copied.Stage == (int)RealTimeNativeCheckpointAckStage.Prepared && transfer.Manifest == null && copied.Number == 0)
                    { transfer.Prepared.TrySetResult(true); return; }
                    if (copied.Stage == (int)RealTimeNativeCheckpointAckStage.Loaded)
                    {
                        if (!transfer.LoadRequested || copied.Number != 0 || !transfer.Transferred.Task.IsCompleted
                            || !transfer.Transferred.Task.Result || transfer.Loaded.Task.IsCompleted) return;
                        transfer.Deadline = DateTime.UtcNow.AddSeconds(60); // Bounded enclosing verification window.
                        transfer.Loaded.TrySetResult(true);
                        return; // NOT canonical verification, Commit or resume.
                    }
                    if (transfer.Sender?.Acknowledge(receivedFrom, copied) != true) return;
                    if (transfer.Sender.IsTransferred)
                    {
                        transfer.Transferred.TrySetResult(true);
                        transfer.Sender.Dispose(); transfer.Sender = null;
                        Logger.LogInformation("Native archive received and verified by client. Epoch={Epoch}, Checkpoint={Checkpoint}. Mechanics not loaded.",
                            expected.Epoch, expected.CheckpointId);
                    }
                }
            });
        }

        private void FailNativeCheckpointTransfer(NativeTransfer transfer, string reason)
        {
            if (transfer == null || !ReferenceEquals(_nativeTransfer, transfer)) return;
            var abort = RealTimeNativeCheckpointLimits.Copy(transfer.Manifest ?? transfer.Prepare);
            abort.Stage = (int)RealTimeNativeCheckpointStage.Abort;
            try { Send(transfer.Peer, abort); }
            catch (Exception error) { Logger.LogWarning(error, "Unable to send native checkpoint abort."); }
            Logger.LogWarning("Native checkpoint transfer aborted. Reason={Reason}; combat remains protected.", reason);
            ResetNativeCheckpointTransfer(); // No resume, phase change or save authorization.
        }

        private void ResetNativeCheckpointTransfer()
        {
            lock (ActionLock)
            {
                var transfer = _nativeTransfer; _nativeTransfer = null;
                transfer?.Sender?.Dispose();
                transfer?.Prepared.TrySetResult(false); transfer?.Transferred.TrySetResult(false);
                transfer?.Loaded.TrySetResult(false);
            }
        }
    }
}
