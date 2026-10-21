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
            public bool VerificationRequested;
            public bool CommitRequested;
            public Func<bool> VerificationOwned;
            public NetworkCombatState Reference;
            public byte[] ReferenceBytes;
            public string ReferenceHash;
            public RealTimeNativeCheckpointAbort Abort;
            public bool AbortExpired;
            public readonly TaskCompletionSource<bool> Prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Transferred = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> ReferenceVerified = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (!await VerifyCurrentNativeReferenceAsync(manifest, barrierOwned)) return null;
            lock (ActionLock)
                return IsCurrentNativeTransfer(owner) ? RealTimeNativeCheckpointLimits.Copy(owner.Manifest) : null;
        }
        private NativeTransfer _nativeTransfer;

        private async Task<bool> CommitCurrentNativeCheckpointAsync(NotifyRealTimeNativeCheckpoint manifest, Func<bool> barrierOwned)
        {
            var started = new TaskCompletionSource<NativeTransfer>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainThreadAccessor.Post(() =>
            {
                lock (ActionLock)
                {
                    var transfer = _nativeTransfer;
                    if (!barrierOwned() || !IsCurrentNativeTransfer(transfer)
                        || !RealTimeNativeCheckpointLimits.SameIdentity(transfer.Manifest, manifest)
                        || transfer.Manifest.ArchiveHash != manifest.ArchiveHash
                        || !transfer.ReferenceVerified.Task.IsCompleted || !transfer.ReferenceVerified.Task.Result)
                    { started.TrySetResult(null); return; }
                    transfer.CommitRequested = true;
                    transfer.Deadline = DateTime.UtcNow.AddSeconds(30); transfer.NextHeader = default;
                    started.TrySetResult(transfer); TickNativeCheckpointTransfer();
                }
            });
            var owner = await started.Task;
            return owner != null && await owner.Committed.Task;
        }

        internal async Task<bool> VerifyCurrentNativeReferenceAsync(NotifyRealTimeNativeCheckpoint expected, Func<bool> barrierOwned)
        {
            if (barrierOwned == null) throw new ArgumentNullException(nameof(barrierOwned));
            if (!RealTimeNativeCheckpointLimits.IsManifest(expected)) throw new InvalidOperationException("native-reference-manifest-invalid");
            var manifest = RealTimeNativeCheckpointLimits.Copy(expected);
            var started = new TaskCompletionSource<NativeTransfer>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainThreadAccessor.Post(() =>
            {
                lock (ActionLock)
                {
                    var transfer = _nativeTransfer;
                    try
                    {
                        if (!barrierOwned() || !IsCurrentNativeTransfer(transfer) || transfer.Manifest == null
                            || !RealTimeNativeCheckpointLimits.SameIdentity(manifest, transfer.Manifest)
                            || manifest.ArchiveHash != transfer.Manifest.ArchiveHash || !GameInteraction.IsPaused
                            || !transfer.Loaded.Task.IsCompleted || !transfer.Loaded.Task.Result
                            || transfer.VerificationRequested)
                        { started.TrySetResult(null); return; }
                        if (transfer.ReferenceBytes == null || transfer.Reference == null
                            || RealTimeNativeCheckpointLimits.Hash(transfer.ReferenceBytes) != transfer.ReferenceHash)
                            throw new InvalidOperationException("native-reference-host-source-invalid");
                        var current = CombatInteraction.GetRealTimeNativeReferenceState();
                        RealTimeAcceptedOrderLedger.RequireUnchanged(transfer.State, transfer.Reference.AcceptedOrderReference,
                            transfer.Session.Id, transfer.Session.LoadedSaveSeed);
                        if (RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = current }) != manifest.CanonicalStateHash
                            || RealTimeCombatTemporalDiff.Summarize(transfer.Reference, current) != null
                            || RealTimeNativeTopologyComparison.Difference(transfer.Reference.NativeTopology, current.NativeTopology) != null)
                            throw new InvalidOperationException("native-reference-host-changed-before-verification");
                        transfer.VerificationOwned = barrierOwned;
                        transfer.VerificationRequested = true;
                        transfer.Deadline = DateTime.UtcNow.AddSeconds(60); transfer.NextHeader = default;
                        started.TrySetResult(transfer);
                        TickNativeCheckpointTransfer();
                    }
                    catch (Exception error)
                    {
                        Logger.LogError(error, "Native reference verification could not start.");
                        FailNativeCheckpointTransfer(transfer, "native-reference-host-not-ready");
                        started.TrySetResult(null);
                    }
                }
            });
            var owner = await started.Task;
            if (owner == null || !await owner.ReferenceVerified.Task) return false;
            lock (ActionLock) return IsCurrentNativeTransfer(owner);
        }

        private bool IsCurrentNativeTransferIdentity(NativeTransfer transfer)
        {
            var session = Game;
            return transfer != null
            && ReferenceEquals(_nativeTransfer, transfer) && RealTimePresentationRuntime.Enabled && IsActive
            && ReferenceEquals(session, transfer.Session) && ReferenceEquals(session?.Combat, transfer.Combat)
            && ReferenceEquals(session?.RealTimeCombat, transfer.State) && session.Id == transfer.Prepare.SessionId
            && session.LoadedSaveSeed == transfer.Prepare.LoadedSaveSeed && transfer.State.Epoch == transfer.Prepare.Epoch;
        }

        private bool IsCurrentNativeTransfer(NativeTransfer transfer)
            => IsCurrentNativeTransferIdentity(transfer) && transfer.Abort == null
            && transfer.State.LastAuthoritativeSequence == transfer.Prepare.LastIncludedSequence
            && transfer.State.Phase == transfer.Phase && transfer.Phase != CombatSynchronizationPhase.Stable
            && transfer.State.Mode == CombatMode.RealTimeWithPause && transfer.BarrierOwned()
            && (!transfer.VerificationRequested || transfer.VerificationOwned?.Invoke() == true);

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
                        var reference = CombatInteraction.GetRealTimeNativeReferenceState();
                        RealTimeNativeTopologyComparison.RequireValid(reference.NativeTopology);
                        reference.AcceptedOrderReference = RealTimeAcceptedOrderLedger.CaptureReference(Game.RealTimeCombat, Game.Id, Game.LoadedSaveSeed);
                        var referenceBytes = RealTimeNativeReferenceCodec.Encode(Mapper.Map<Networking.Messages.Contracts.NetworkCombatState>(reference));
                        var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = reference });
                        var owner = new NativeTransfer
                        {
                            Session = Game,
                            State = Game.RealTimeCombat,
                            Combat = Game.Combat,
                            BarrierOwned = barrierOwned,
                            Phase = Game.RealTimeCombat.Phase,
                            Peer = peers[0].Id,
                            Reference = reference, ReferenceBytes = referenceBytes,
                            ReferenceHash = RealTimeNativeCheckpointLimits.Hash(referenceBytes),
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
                            var current = CombatInteraction.GetRealTimeNativeReferenceState();
                            RealTimeAcceptedOrderLedger.RequireUnchanged(transfer.State, transfer.Reference.AcceptedOrderReference,
                                transfer.Session.Id, transfer.Session.LoadedSaveSeed);
                            var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = current });
                            if (hash != transfer.Prepare.CanonicalStateHash || RealTimeCombatTemporalDiff.Summarize(transfer.Reference, current) != null
                                || RealTimeNativeTopologyComparison.Difference(transfer.Reference.NativeTopology, current.NativeTopology) != null)
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
                if (!IsCurrentNativeTransferIdentity(transfer)) { ResetNativeCheckpointTransfer(); return; }
                if (transfer.Abort != null) { TickNativeCheckpointAbort(transfer); return; }
                if (!IsCurrentNativeTransfer(transfer))
                { FailNativeCheckpointTransfer(transfer, "native-transfer-barrier-revoked"); return; }
                if (DateTime.UtcNow >= transfer.Deadline)
                { FailNativeCheckpointTransfer(transfer, "native-transfer-timeout"); return; }
                if (!transfer.CommitRequested && transfer.ReferenceVerified.Task.IsCompleted && transfer.ReferenceVerified.Task.Result) return;
                if (!transfer.VerificationRequested && transfer.Loaded.Task.IsCompleted && transfer.Loaded.Task.Result) return;
                try
                {
                    if (DateTime.UtcNow >= transfer.NextHeader)
                    {
                        transfer.State.RecoveryStatus = transfer.CommitRequested ? "committed"
                            : transfer.VerificationRequested ? "verifying" : transfer.LoadRequested ? "loading" : "rebuilding";
                        transfer.NextHeader = DateTime.UtcNow.AddSeconds(1);
                        var header = RealTimeNativeCheckpointLimits.Copy(transfer.Manifest ?? transfer.Prepare);
                        if (transfer.LoadRequested) header.Stage = (int)RealTimeNativeCheckpointStage.Load;
                        if (transfer.VerificationRequested)
                        {
                            header.Stage = (int)RealTimeNativeCheckpointStage.Verify;
                            header.ReferenceStateBytes = (byte[])transfer.ReferenceBytes.Clone();
                            header.ReferenceStateHash = transfer.ReferenceHash;
                        }
                        if (transfer.CommitRequested)
                        {
                            header.Stage = (int)RealTimeNativeCheckpointStage.Commit;
                            header.ReferenceStateBytes = null;
                        }
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
                || ack.ReferenceStateHash != null && !RealTimeNativeCheckpointLimits.IsHash(ack.ReferenceStateHash)
                || !Enum.IsDefined(typeof(RealTimeNativeCheckpointAckStage), ack.Stage)) return;
            var copied = ProtoBuf.Serializer.DeepClone(ack);
            _mainThreadAccessor.Post(() =>
            {
                lock (ActionLock)
                {
                    var transfer = _nativeTransfer;
                    if (IsCurrentNativeTransferIdentity(transfer) && transfer.Abort != null)
                    {
                        if (!transfer.AbortExpired && transfer.Abort.Acknowledge(receivedFrom, copied))
                        {
                            transfer.Aborted.TrySetResult(true);
                            Logger.LogInformation("Native checkpoint abort acknowledged. Checkpoint={Checkpoint}; no resume or save authorization.",
                                transfer.Prepare.CheckpointId);
                        }
                        return;
                    }
                    if (!IsCurrentNativeTransfer(transfer) || receivedFrom != transfer.Peer) return;
                    var expected = transfer.Manifest ?? transfer.Prepare;
                    if (!RealTimeNativeCheckpointLimits.Matches(expected, copied)) return;
                    if (copied.Stage == (int)RealTimeNativeCheckpointAckStage.Failed)
                    { FailNativeCheckpointTransfer(transfer, "native-transfer-client-refused"); return; }
                    if (copied.Stage == (int)RealTimeNativeCheckpointAckStage.Committed)
                    {
                        if (!transfer.CommitRequested || copied.Number != 0 || copied.Reason != null
                            || copied.ReferenceStateHash != transfer.ReferenceHash
                            || copied.CanonicalStateHash != expected.CanonicalStateHash) return;
                        transfer.Committed.TrySetResult(true);
                        return;
                    }
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
                    if (copied.Stage == (int)RealTimeNativeCheckpointAckStage.ReferenceVerified)
                    {
                        if (!transfer.VerificationRequested || copied.Number != 0 || !transfer.Loaded.Task.IsCompleted
                            || !transfer.Loaded.Task.Result || transfer.ReferenceVerified.Task.IsCompleted) return;
                        if (copied.ReferenceStateHash != transfer.ReferenceHash || copied.CanonicalStateHash != expected.CanonicalStateHash)
                        { FailNativeCheckpointTransfer(transfer, "native-reference-client-hash-differs"); return; }
                        transfer.Deadline = DateTime.UtcNow.AddSeconds(60);
                        transfer.ReferenceVerified.TrySetResult(true);
                        return; // Still no mechanical certificate, Commit or resume.
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
            if (transfer.Abort != null) return; // Never renew a lost abort's retry budget.
            transfer.Sender?.Dispose(); transfer.Sender = null;
            transfer.Reference = null; transfer.ReferenceBytes = null; transfer.ReferenceHash = null;
            transfer.Prepared.TrySetResult(false); transfer.Transferred.TrySetResult(false);
            transfer.Loaded.TrySetResult(false); transfer.ReferenceVerified.TrySetResult(false);
            transfer.Committed.TrySetResult(false);
            transfer.Abort = new RealTimeNativeCheckpointAbort(transfer.Peer, transfer.Manifest ?? transfer.Prepare, DateTime.UtcNow);
            Logger.LogWarning("Native checkpoint transfer abort requested. Reason={Reason}; awaiting revoked client work, combat remains protected.", reason);
            TickNativeCheckpointAbort(transfer);
        }

        private void TickNativeCheckpointAbort(NativeTransfer transfer)
        {
            if (transfer.AbortExpired) return;
            NotifyRealTimeNativeCheckpoint abort;
            try { abort = transfer.Abort.GetDue(DateTime.UtcNow); }
            catch (InvalidOperationException error)
            {
                Logger.LogWarning(error, "Native checkpoint abort was not acknowledged; protected recovery remains incomplete.");
                transfer.AbortExpired = true;
                transfer.Aborted.TrySetResult(false);
                // Retain the checkpoint tombstone until identity/epoch reset.
                // A timeout is not permission for a legacy repair ACK to resume.
                return;
            }
            if (abort == null) return;
            try { Send(transfer.Peer, abort); }
            catch (Exception error) { Logger.LogWarning(error, "Unable to send native checkpoint abort; bounded retry remains active."); }
        }

        internal async Task<bool> AbortCurrentNativeCheckpointAsync()
        {
            var started = new TaskCompletionSource<NativeTransfer>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainThreadAccessor.Post(() =>
            {
                lock (ActionLock)
                {
                    var transfer = _nativeTransfer;
                    if (!IsCurrentNativeTransferIdentity(transfer)) { started.TrySetResult(null); return; }
                    FailNativeCheckpointTransfer(transfer, "native-transfer-coordinator-aborted");
                    started.TrySetResult(transfer);
                }
            });
            var owner = await started.Task;
            return owner != null && await owner.Aborted.Task;
        }

        private void ResetNativeCheckpointTransfer()
        {
            lock (ActionLock)
            {
                var transfer = _nativeTransfer; _nativeTransfer = null;
                transfer?.Sender?.Dispose();
                transfer?.Prepared.TrySetResult(false); transfer?.Transferred.TrySetResult(false);
                transfer?.Loaded.TrySetResult(false);
                transfer?.ReferenceVerified.TrySetResult(false);
                transfer?.Aborted.TrySetResult(false);
                transfer?.Committed.TrySetResult(false);
            }
        }
    }
}
