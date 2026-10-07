using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerClient
    {
        private NetworkGame _nativeReceiveSession;
        private NetworkCombat _nativeReceiveCombat;
        private NetworkRealTimeCombatState _nativeReceiveState;
        private CombatSynchronizationPhase _nativeReceivePhase;
        private NotifyRealTimeNativeCheckpoint _nativeReceivePrepare;
        private NotifyRealTimeNativeCheckpoint _nativeReceiveManifest;
        private RealTimeNativeCheckpointReceiver _nativeReceiver;
        private RealTimeMemorySaver _nativeReceivedSource;
        private bool _nativeReceivePrepared;
        private bool _nativeReceiveFailed;
        private DateTime _nativeReceiveDeadline;
        private RealTimeNativeLoadOperation _nativeLoadOperation;
        private NotifyRealTimeNativeCheckpoint _nativeReferenceRequest;
        private ulong? _nativeReferenceVerifiedHash;

        private bool IsCurrentNativeReceive()
        {
            var session = Game;
            return _nativeReceivePrepare != null && RealTimePresentationRuntime.Enabled && IsActive
                && ReferenceEquals(session, _nativeReceiveSession) && ReferenceEquals(session?.Combat, _nativeReceiveCombat)
                && ReferenceEquals(session?.RealTimeCombat, _nativeReceiveState) && session.Id == _nativeReceivePrepare.SessionId
                && session.LoadedSaveSeed == _nativeReceivePrepare.LoadedSaveSeed && session.RealTimeCombat.Epoch == _nativeReceivePrepare.Epoch
                && session.RealTimeCombat.Phase == _nativeReceivePhase && _nativeReceivePhase != CombatSynchronizationPhase.Stable
                && session.RealTimeCombat.Mode == CombatMode.RealTimeWithPause;
        }

        private void OnNotifyNativeCheckpoint(long receivedFrom, NotifyRealTimeNativeCheckpoint frame)
        {
            if (!RealTimePresentationRuntime.Enabled || receivedFrom != NetworkConstants.HostPlayerId
                || !RealTimeNativeCheckpointLimits.HasIdentity(frame)
                || !Enum.IsDefined(typeof(RealTimeNativeCheckpointStage), frame.Stage)
                || frame.ArchiveHash?.Length > 64 || frame.ReferenceStateHash?.Length > 64
                || frame.ReferenceStateBytes?.Length > RealTimeNativeReferenceCodec.MaximumBytes
                || frame.Stage != (int)RealTimeNativeCheckpointStage.Verify
                    && (frame.ReferenceStateBytes != null || frame.ReferenceStateHash != null)) return;
            var copied = RealTimeNativeCheckpointLimits.Copy(frame);
            _mainThreadAccessor.Post(() =>
            {
                lock (ActionLock)
                {
                    try
                    {
                        if (Game?.Combat == null || Game.Id != copied.SessionId || Game.LoadedSaveSeed != copied.LoadedSaveSeed
                            || Game.RealTimeCombat?.Epoch != copied.Epoch || Game.RealTimeCombat.Mode != CombatMode.RealTimeWithPause
                            || Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Stable) return;
                        var fingerprint = Game.Players.FirstOrDefault(player => player.Id == Game.LocalPlayerId)?.ContentState?.RealTimeBuildFingerprint;
                        if (fingerprint != copied.BuildFingerprint) return;
                        if (_nativeReceivePrepare != null && !IsCurrentNativeReceive()) ResetNativeCheckpointReceive();
                        if (copied.Stage == (int)RealTimeNativeCheckpointStage.Prepare)
                        {
                            if (!RealTimeNativeCheckpointLimits.IsPrepare(copied)) return;
                            if (_nativeReceivePrepare != null)
                            {
                                // Exactly one checkpoint per epoch. A competing UUID
                                // may not replace partially received/restored data.
                                if (!RealTimeNativeCheckpointLimits.SameIdentity(_nativeReceivePrepare, copied)) return;
                                if (_nativeReceiveFailed) SendNativeCheckpointAck(copied, RealTimeNativeCheckpointAckStage.Failed);
                                else if (_nativeReceivePrepared) SendNativeCheckpointAck(copied, RealTimeNativeCheckpointAckStage.Prepared);
                                return;
                            }
                            _nativeReceivePrepare = copied; _nativeReceiveSession = Game;
                            _nativeReceiveCombat = Game.Combat; _nativeReceiveState = Game.RealTimeCombat;
                            _nativeReceivePhase = Game.RealTimeCombat.Phase; _nativeReceiveDeadline = DateTime.UtcNow.AddSeconds(60);
                            PauseNativeCheckpointReceive(copied);
                            return;
                        }
                        if (!IsCurrentNativeReceive() || !RealTimeNativeCheckpointLimits.SameIdentity(_nativeReceivePrepare, copied)) return;
                        if (copied.Stage == (int)RealTimeNativeCheckpointStage.Abort)
                        {
                            if (_nativeReceiveManifest != null && copied.ArchiveHash != _nativeReceiveManifest.ArchiveHash) return;
                            FailNativeCheckpointReceive("native-transfer-host-aborted", copied, notify: false);
                            return;
                        }
                        if (copied.Stage == (int)RealTimeNativeCheckpointStage.Load)
                        {
                            var requested = RealTimeNativeCheckpointLimits.AsManifest(copied);
                            if (!_nativeReceivePrepared || _nativeReceiveFailed || _nativeReceivedSource == null
                                || requested == null || _nativeReceiver?.MatchesManifest(requested) != true) return;
                            if (_nativeLoadOperation != null)
                            {
                                if (_nativeLoadOperation.IsLoaded)
                                    SendNativeCheckpointAck(requested, RealTimeNativeCheckpointAckStage.Loaded);
                                return; // Pending or failed: never schedule a second native world load.
                            }
                            var receivedManifest = _nativeReceiveManifest;
                            bool Authorized()
                            {
                                lock (ActionLock)
                                    return IsCurrentNativeReceive() && !_nativeReceiveFailed
                                        && ReferenceEquals(_nativeReceiveManifest, receivedManifest)
                                        && _nativeReceiver?.MatchesManifest(requested) == true;
                            }
                            var operation = new RealTimeNativeLoadOperation(Authorized);
                            _nativeLoadOperation = operation;
                            _nativeReceiveDeadline = DateTime.MaxValue;
                            RunNativeCheckpointLoad(operation, requested, Authorized);
                            return;
                        }
                        if (copied.Stage == (int)RealTimeNativeCheckpointStage.Verify)
                        {
                            var manifest = RealTimeNativeCheckpointLimits.ReferenceManifest(copied);
                            if (!_nativeReceivePrepared || _nativeReceiveFailed || _nativeReceivedSource == null
                                || manifest == null || _nativeReceiver?.MatchesManifest(manifest) != true
                                || _nativeLoadOperation?.IsLoaded != true) return;
                            if (_nativeReferenceRequest != null)
                            {
                                if (_nativeReferenceRequest.ReferenceStateHash != copied.ReferenceStateHash)
                                { FailNativeCheckpointReceive("native-reference-conflicting-payload"); return; }
                                if (_nativeReferenceVerifiedHash.HasValue)
                                    SendNativeCheckpointAck(manifest, RealTimeNativeCheckpointAckStage.ReferenceVerified,
                                        canonicalStateHash: _nativeReferenceVerifiedHash.Value, referenceStateHash: copied.ReferenceStateHash);
                                return; // One restore per checkpoint, including while pending/failed.
                            }
                            _nativeReferenceRequest = copied;
                            _nativeReceiveDeadline = DateTime.UtcNow.AddSeconds(60);
                            VerifyNativeCheckpointReference(copied, manifest);
                            return;
                        }
                        if (!_nativeReceivePrepared) return;
                        if (_nativeReceiveFailed) { SendNativeCheckpointAck(copied, RealTimeNativeCheckpointAckStage.Failed); return; }
                        if (_nativeReceiveManifest != null)
                        {
                            if (_nativeReceiver?.MatchesManifest(copied) != true)
                            { FailNativeCheckpointReceive("native-transfer-conflicting-manifest", copied); return; }
                            if (_nativeReceivedSource != null) SendNativeCheckpointAck(copied, RealTimeNativeCheckpointAckStage.Received);
                            return;
                        }
                        _nativeReceiver = new RealTimeNativeCheckpointReceiver(_nativeReceivePrepare, copied);
                        _nativeReceiveManifest = copied;
                        _nativeReceiveDeadline = DateTime.UtcNow + RealTimeNativeCheckpointLimits.Deadline(copied.ArchiveBytes);
                    }
                    catch (Exception error)
                    {
                        Logger.LogError(error, "Native checkpoint manifest refused before any mechanical load.");
                        FailNativeCheckpointReceive("native-transfer-manifest-refused", copied);
                    }
                }
            });
        }

        private async void PauseNativeCheckpointReceive(NotifyRealTimeNativeCheckpoint prepare)
        {
            try
            {
                await GameInteraction.SetPauseAsync(true);
                _mainThreadAccessor.Post(() =>
                {
                    lock (ActionLock)
                    {
                        if (!IsCurrentNativeReceive() || !ReferenceEquals(_nativeReceivePrepare, prepare) || _nativeReceiveFailed) return;
                        if (!GameInteraction.IsPaused) { FailNativeCheckpointReceive("native-transfer-pause-not-confirmed"); return; }
                        _nativeReceivePrepared = true;
                        SendNativeCheckpointAck(prepare, RealTimeNativeCheckpointAckStage.Prepared);
                    }
                });
            }
            catch (Exception error)
            {
                Logger.LogError(error, "Unable to pause client before native checkpoint transfer.");
                _mainThreadAccessor.Post(() =>
                {
                    lock (ActionLock)
                        if (ReferenceEquals(_nativeReceivePrepare, prepare)) FailNativeCheckpointReceive("native-transfer-pause-failed");
                });
            }
        }

        private async void RunNativeCheckpointLoad(RealTimeNativeLoadOperation operation,
            NotifyRealTimeNativeCheckpoint manifest, Func<bool> authorized)
        {
            try
            {
                await operation.RunAsync(() => LoadTransferredNativeCheckpointAsync(manifest, authorized));
                _mainThreadAccessor.Post(() =>
                {
                    lock (ActionLock)
                    {
                        if (!ReferenceEquals(_nativeLoadOperation, operation) || !operation.IsLoaded) return;
                        SendNativeCheckpointAck(manifest, RealTimeNativeCheckpointAckStage.Loaded);
                        Logger.LogInformation("Native client world loaded. Checkpoint={Checkpoint}; verification and Commit still required.", manifest.CheckpointId);
                    }
                });
            }
            catch (Exception error)
            {
                Logger.LogError(error, "Native checkpoint reconstruction failed; no resume or save authorization.");
                _mainThreadAccessor.Post(() =>
                {
                    lock (ActionLock)
                        if (ReferenceEquals(_nativeLoadOperation, operation) && IsCurrentNativeReceive())
                            FailNativeCheckpointReceive("native-load-reconstruction-failed");
                });
            }
        }

        private async void VerifyNativeCheckpointReference(NotifyRealTimeNativeCheckpoint request,
            NotifyRealTimeNativeCheckpoint manifest)
        {
            bool Authorized()
            {
                lock (ActionLock)
                    return ReferenceEquals(_nativeReferenceRequest, request) && IsCurrentNativeReceive() && !_nativeReceiveFailed;
            }
            try
            {
                var reference = Mapper.Map<NetworkCombatState>(RealTimeNativeReferenceCodec.Decode(request.ReferenceStateBytes));
                var hash = await RestoreLoadedNativeReferenceAsync(manifest, reference, Authorized);
                _mainThreadAccessor.Post(() =>
                {
                    lock (ActionLock)
                    {
                        if (!Authorized()) return;
                        if (!hash.HasValue) { FailNativeCheckpointReceive("native-reference-restoration-not-confirmed"); return; }
                        _nativeReferenceVerifiedHash = hash;
                        _nativeReceiveDeadline = DateTime.UtcNow.AddSeconds(60);
                        SendNativeCheckpointAck(manifest, RealTimeNativeCheckpointAckStage.ReferenceVerified,
                            canonicalStateHash: hash.Value, referenceStateHash: request.ReferenceStateHash);
                        Logger.LogInformation("Native reference data verified. Checkpoint={Checkpoint}; mechanical certificate and Commit still required.", manifest.CheckpointId);
                    }
                });
            }
            catch (Exception error)
            {
                Logger.LogError(error, "Native reference verification failed; combat remains protected.");
                _mainThreadAccessor.Post(() =>
                {
                    lock (ActionLock)
                        if (Authorized()) FailNativeCheckpointReceive("native-reference-verification-failed");
                });
            }
        }

        private void OnNotifyNativeCheckpointChunk(long receivedFrom, NotifyRealTimeNativeCheckpointChunk chunk)
        {
            if (!RealTimePresentationRuntime.Enabled || receivedFrom != NetworkConstants.HostPlayerId || chunk == null
                || chunk.Data == null || chunk.Data.Length > RealTimeNativeCheckpointLimits.ChunkBytes
                || chunk.SessionId?.Length > 128 || !Guid.TryParseExact(chunk.CheckpointId, "N", out _)
                || !RealTimeNativeCheckpointLimits.IsHash(chunk.ArchiveHash) || !RealTimeNativeCheckpointLimits.IsHash(chunk.ChunkHash)) return;
            var copied = ProtoBuf.Serializer.DeepClone(chunk); // Bounded payload owned before posting.
            _mainThreadAccessor.Post(() =>
            {
                lock (ActionLock)
                {
                    try
                    {
                        var manifest = _nativeReceiveManifest;
                        if (!IsCurrentNativeReceive() || !_nativeReceivePrepared || _nativeReceiveFailed || manifest == null
                            || copied.SessionId != manifest.SessionId || copied.LoadedSaveSeed != manifest.LoadedSaveSeed
                            || copied.Epoch != manifest.Epoch || copied.CheckpointId != manifest.CheckpointId
                            || copied.ArchiveHash != manifest.ArchiveHash) return;
                        if (_nativeReceivedSource != null)
                        { SendNativeCheckpointAck(manifest, RealTimeNativeCheckpointAckStage.Received); return; }
                        if (!_nativeReceiver.Accept(copied)) return; // No ACK: bounded retransmission repairs a damaged/missing fragment.
                        SendNativeCheckpointAck(manifest, RealTimeNativeCheckpointAckStage.Chunk, copied.Number);
                        if (!_nativeReceiver.IsComplete) return;
                        var archive = _nativeReceiver.TakeCompleted();
                        // Decode files only; do not deserialize native game objects,
                        // clear the stash or invoke Game.LoadGame here.
                        _nativeReceivedSource = RealTimeNativeCheckpointCodec.Decode(archive, manifest.ArchiveHash);
                        _nativeReceiveDeadline = DateTime.MaxValue;
                        SendNativeCheckpointAck(manifest, RealTimeNativeCheckpointAckStage.Received);
                        Logger.LogInformation("Native checkpoint bytes verified. Epoch={Epoch}, Checkpoint={Checkpoint}. Awaiting protected native load.",
                            manifest.Epoch, manifest.CheckpointId);
                    }
                    catch (Exception error)
                    {
                        Logger.LogError(error, "Native checkpoint archive refused; existing world remains paused.");
                        FailNativeCheckpointReceive("native-transfer-archive-refused");
                    }
                }
            });
        }

        private void SendNativeCheckpointAck(NotifyRealTimeNativeCheckpoint frame, RealTimeNativeCheckpointAckStage stage,
            int number = 0, ulong canonicalStateHash = 0, string referenceStateHash = null)
            => Send(new ClientRealTimeNativeCheckpointAck
            {
                SessionId = frame.SessionId,
                LoadedSaveSeed = frame.LoadedSaveSeed,
                Epoch = frame.Epoch,
                CheckpointId = frame.CheckpointId,
                ArchiveHash = frame.ArchiveHash,
                Stage = (int)stage,
                Number = number,
                CanonicalStateHash = canonicalStateHash,
                ReferenceStateHash = referenceStateHash
            });

        private void FailNativeCheckpointReceive(string reason, NotifyRealTimeNativeCheckpoint frame = null, bool notify = true)
        {
            if (!IsCurrentNativeReceive()) return;
            _nativeReceiveFailed = true; _nativeReceiveDeadline = DateTime.MaxValue;
            _nativeReceiver?.Dispose(); _nativeReceivedSource?.Dispose(); _nativeReceivedSource = null;
            if (notify) SendNativeCheckpointAck(frame ?? _nativeReceiveManifest ?? _nativeReceivePrepare, RealTimeNativeCheckpointAckStage.Failed);
            Logger.LogWarning("Native checkpoint reception aborted. Reason={Reason}; no mechanical load or resume.", reason);
        }

        private void TickNativeCheckpointReceive()
        {
            lock (ActionLock)
            {
                if (_nativeReceivePrepare == null) return;
                if (!IsCurrentNativeReceive()) { ResetNativeCheckpointReceive(); return; }
                if (DateTime.UtcNow >= _nativeReceiveDeadline) FailNativeCheckpointReceive("native-transfer-receive-timeout");
            }
        }

        internal RealTimeMemorySaver CloneTransferredNativeCheckpoint(NotifyRealTimeNativeCheckpoint expected)
        {
            lock (ActionLock)
            {
                if (!IsCurrentNativeReceive() || _nativeReceiveFailed || _nativeReceivedSource == null
                    || _nativeReceiver?.MatchesManifest(expected) != true)
                    throw new InvalidOperationException("native-transfer-source-not-ready-for-protected-load");
                return (RealTimeMemorySaver)_nativeReceivedSource.Clone();
            }
        }

        private void ResetNativeCheckpointReceive()
        {
            lock (ActionLock)
            {
                _nativeReceiver?.Dispose(); _nativeReceivedSource?.Dispose();
                _nativeReceiver = null; _nativeReceivedSource = null; _nativeReceivePrepare = null; _nativeReceiveManifest = null;
                _nativeReceiveSession = null; _nativeReceiveCombat = null; _nativeReceiveState = null;
                _nativeReceivePrepared = false; _nativeReceiveFailed = false;
                _nativeLoadOperation = null;
                _nativeReferenceRestorationOwner = null;
                _nativeReferenceRequest = null; _nativeReferenceVerifiedHash = null;
            }
        }
    }
}
