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
        private NotifyRealTimeNativeCheckpoint _nativeAbortRequest;
        private bool _nativeAbortAcknowledged;
        private bool _nativePausePending;
        private DateTime _nativeAbortAckNextAttempt;
        private bool _nativeCommitPending;
        private bool _nativeCommitted;

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
                || frame.Stage != (int)RealTimeNativeCheckpointStage.Verify && frame.Stage != (int)RealTimeNativeCheckpointStage.Commit
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
                        if (copied.Stage == (int)RealTimeNativeCheckpointStage.Abort)
                        {
                            var source = RealTimeNativeCheckpointLimits.AbortSource(copied);
                            if (source == null) return;
                            var repeatedRequest = _nativeAbortRequest != null;
                            if (_nativeReceivePrepare == null)
                            {
                                // A lost Prepare must not make Abort unacknowledgeable.
                                // Retain a tombstone, never allocate/load archive data.
                                var prepare = RealTimeNativeCheckpointLimits.Copy(source);
                                prepare.Stage = (int)RealTimeNativeCheckpointStage.Prepare;
                                prepare.ArchiveHash = null; prepare.ArchiveBytes = prepare.ChunkBytes = prepare.ChunkCount = 0;
                                _nativeReceivePrepare = prepare; _nativeReceiveSession = Game;
                                _nativeReceiveCombat = Game.Combat; _nativeReceiveState = Game.RealTimeCombat;
                                _nativeReceivePhase = Game.RealTimeCombat.Phase;
                                if (RealTimeNativeCheckpointLimits.IsManifest(source)) _nativeReceiveManifest = source;
                                _nativeAbortRequest = copied;
                                FailNativeCheckpointReceive("native-transfer-host-aborted-before-prepare", notify: false);
                                PauseNativeCheckpointReceive(prepare);
                            }
                            else
                            {
                                if (!RealTimeNativeCheckpointLimits.SameIdentity(_nativeReceivePrepare, copied)) return;
                                var manifest = _nativeReceiveManifest;
                                if (manifest != null && (source.ArchiveHash != manifest.ArchiveHash
                                    || source.ArchiveBytes != manifest.ArchiveBytes || source.ChunkCount != manifest.ChunkCount
                                    || source.ChunkBytes != manifest.ChunkBytes)) return;
                                if (_nativeAbortRequest != null && (_nativeAbortRequest.ArchiveHash != copied.ArchiveHash
                                    || _nativeAbortRequest.ArchiveBytes != copied.ArchiveBytes
                                    || _nativeAbortRequest.ChunkCount != copied.ChunkCount)) return;
                                if (manifest == null && RealTimeNativeCheckpointLimits.IsManifest(source)) _nativeReceiveManifest = source;
                                _nativeAbortRequest = copied;
                                FailNativeCheckpointReceive("native-transfer-host-aborted", notify: false);
                            }
                            TryAcknowledgeNativeAbort(repeat: repeatedRequest);
                            return;
                        }
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
                            Game.RealTimeCombat.RecoveryStatus = "rebuilding";
                            _nativeReceiveCombat = Game.Combat; _nativeReceiveState = Game.RealTimeCombat;
                            _nativeReceivePhase = Game.RealTimeCombat.Phase; _nativeReceiveDeadline = DateTime.UtcNow.AddSeconds(60);
                            PauseNativeCheckpointReceive(copied);
                            return;
                        }
                        if (!IsCurrentNativeReceive() || !RealTimeNativeCheckpointLimits.SameIdentity(_nativeReceivePrepare, copied)) return;
                        if (copied.Stage == (int)RealTimeNativeCheckpointStage.Commit)
                        {
                            var manifest = RealTimeNativeCheckpointLimits.Copy(copied, includeReference: false);
                            manifest.Stage = (int)RealTimeNativeCheckpointStage.Manifest;
                            if (!RealTimeNativeCheckpointLimits.IsManifest(manifest) || copied.ReferenceStateBytes != null
                                || _nativeReceiveFailed || _nativeAbortRequest != null
                                || _nativeReceiver?.MatchesManifest(manifest) != true
                                || _nativeReferenceVerifiedHash != copied.CanonicalStateHash
                                || copied.ReferenceStateHash != _nativeReferenceRequest?.ReferenceStateHash
                                || !RealTimeNativeCheckpointLimits.IsHash(copied.ReferenceStateHash)) return;
                            if (_nativeCommitted)
                                SendNativeCheckpointAck(manifest, RealTimeNativeCheckpointAckStage.Committed,
                                    canonicalStateHash: copied.CanonicalStateHash, referenceStateHash: copied.ReferenceStateHash);
                            else if (!_nativeCommitPending) CommitNativeCheckpointReference(copied, manifest);
                            return;
                        }
                        if (copied.Stage == (int)RealTimeNativeCheckpointStage.Load)
                        {
                            var requested = RealTimeNativeCheckpointLimits.AsManifest(copied);
                            Game.RealTimeCombat.RecoveryStatus = "loading";
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
                            Game.RealTimeCombat.RecoveryStatus = "verifying";
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
            _nativePausePending = true;
            try
            {
                await GameInteraction.SetPauseAsync(true);
                _mainThreadAccessor.Post(() =>
                {
                    lock (ActionLock)
                    {
                        if (!ReferenceEquals(_nativeReceivePrepare, prepare)) return;
                        _nativePausePending = false;
                        if (!IsCurrentNativeReceive()) return;
                        if (_nativeReceiveFailed) { TryAcknowledgeNativeAbort(); return; }
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
                    {
                        if (!ReferenceEquals(_nativeReceivePrepare, prepare)) return;
                        _nativePausePending = false;
                        FailNativeCheckpointReceive("native-transfer-pause-failed");
                        TryAcknowledgeNativeAbort();
                    }
                });
            }
        }

        private async void RunNativeCheckpointLoad(RealTimeNativeLoadOperation operation,
            NotifyRealTimeNativeCheckpoint manifest, Func<bool> authorized)
        {
            try
            {
                await operation.RunAsync(() => LoadTransferredNativeCheckpointAsync(manifest, authorized, operation));
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

        private async void CommitNativeCheckpointReference(NotifyRealTimeNativeCheckpoint request, NotifyRealTimeNativeCheckpoint manifest)
        {
            _nativeCommitPending = true;
            var operation = _nativeLoadOperation;
            bool Authorized() => IsCurrentNativeReceive() && !_nativeReceiveFailed
                && ReferenceEquals(_nativeLoadOperation, operation) && operation?.IsLoaded == true
                && _nativeReferenceVerifiedHash == request.CanonicalStateHash;
            try
            {
                var reference = Mapper.Map<NetworkCombatState>(RealTimeNativeReferenceCodec.Decode(_nativeReferenceRequest.ReferenceStateBytes));
                var receipt = operation?.Receipt;
                var committed = receipt != null && await CombatInteraction.TryCommitNativeReferenceStateAsync(
                    receipt.World, receipt.RepairObligation, reference, Authorized);
                _mainThreadAccessor.Post(() =>
                {
                    lock (ActionLock)
                    {
                        if (!Authorized()) return;
                        _nativeCommitPending = false;
                        if (!committed) { FailNativeCheckpointReceive("native-commit-not-confirmed"); return; }
                        _nativeCommitted = true;
                        _localCriticalFaultState = null;
                        Game.RealTimeCombat.CanonicalStateHash = request.CanonicalStateHash;
                        Game.RealTimeCombat.RecoveryStatus = "committed";
                        _nativeReceiveDeadline = DateTime.UtcNow.AddSeconds(30);
                        SendNativeCheckpointAck(manifest, RealTimeNativeCheckpointAckStage.Committed,
                            canonicalStateHash: request.CanonicalStateHash, referenceStateHash: request.ReferenceStateHash);
                    }
                });
            }
            catch (Exception error)
            {
                Logger.LogError(error, "Native reconstruction Commit refused. No resume authorized.");
                _mainThreadAccessor.Post(() => { lock (ActionLock) { _nativeCommitPending = false;
                    if (Authorized()) FailNativeCheckpointReceive("native-commit-failed"); } });
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
            if (notify && _nativeAbortRequest == null)
                SendNativeCheckpointAck(frame ?? _nativeReceiveManifest ?? _nativeReceivePrepare, RealTimeNativeCheckpointAckStage.Failed);
            Logger.LogWarning("Native checkpoint reception revoked. Reason={Reason}; client remains protected.", reason);
        }

        private void TryAcknowledgeNativeAbort(bool repeat = false)
        {
            if (_nativeAbortRequest == null || !IsCurrentNativeReceive() || !_nativeReceiveFailed
                || _nativePausePending || !GameInteraction.IsPaused || RealTimeNativeLoadLease.IsActive
                || _nativeLoadOperation != null && !_nativeLoadOperation.CanAcknowledgeAbort
                || _nativeReferenceRestorationOwner != null || _nativeCommitPending || _nativeAbortAcknowledged && !repeat
                || DateTime.UtcNow < _nativeAbortAckNextAttempt) return;
            try
            {
                SendNativeCheckpointAck(_nativeAbortRequest, RealTimeNativeCheckpointAckStage.Aborted);
                _nativeAbortAcknowledged = true;
                _nativeAbortAckNextAttempt = default;
                _nativeCommitPending = false; _nativeCommitted = false;
            }
            catch (Exception error)
            {
                _nativeAbortAckNextAttempt = DateTime.UtcNow.AddSeconds(1);
                Logger.LogWarning(error, "Unable to acknowledge native abort; client remains protected.");
            }
        }

        private void TickNativeCheckpointReceive()
        {
            lock (ActionLock)
            {
                if (_nativeReceivePrepare == null) return;
                if (!IsCurrentNativeReceive()) { ResetNativeCheckpointReceive(); return; }
                if (_nativeAbortRequest != null) { TryAcknowledgeNativeAbort(); return; }
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
                _nativeAbortRequest = null; _nativeAbortAcknowledged = false; _nativePausePending = false;
                _nativeAbortAckNextAttempt = default;
                _nativeCommitPending = false; _nativeCommitted = false;
            }
        }
    }
}
