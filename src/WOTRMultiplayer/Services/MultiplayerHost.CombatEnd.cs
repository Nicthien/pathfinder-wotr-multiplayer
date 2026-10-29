using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerHost
    {
        private NetworkGame _endSession;
        private NetworkCombat _endCombat;
        private NetworkRealTimeCombatState _endState;
        private int _endSaveSeed;
        private bool _endCaptureQueued;
        private DateTime _endDeadline;
        private DateTime _endNextSend;
        private bool _endFailed;
        private NotifyRealTimeCombatEnd _endFrame;
        private RealTimeCombatEndBarrier _endBarrier;
        private long[] _endPeers;
        private string[] _endParticipantIds;
        private NetworkCombatState _endFinalState;
        private bool _endNativeReconstructionQueued;

        protected override bool TryDeferRealTimeCombatEnd()
        {
            if (!RealTimePresentationRuntime.Enabled || Game?.Combat?.Stage != NetworkCombatStage.Running
                || Game.RealTimeCombat == null || !(Game.RealTimeCombat.Mode == CombatMode.RealTimeWithPause
                    || Game.RealTimeCombat.PreviousMode == CombatMode.RealTimeWithPause
                    || Game.RealTimeCombat.CombatEndPending)) return false;
            if (!Game.RealTimeCombat.CombatEndPending && _rejoin == null && !_waitingForRealTimePeer
                && !GetSyncedPlayers().Any(player => player.Id != Game.LocalPlayerId))
            {
                // No replica needs a final checkpoint. Use the original engine
                // combat-end path instead of blocking input on unrelated or lingering
                // projectiles/commands. A barrier already created for peers is NEVER
                // bypassed if those peers subsequently disconnect.
                Logger.LogInformation("[CombatMode] Solo combat end uses the normal completion path; no synchronization barrier created.");
                return false;
            }
            if (!ReferenceEquals(_endSession, Game) || !ReferenceEquals(_endCombat, Game.Combat)
                || !ReferenceEquals(_endState, Game.RealTimeCombat) || _endSaveSeed != Game.LoadedSaveSeed)
            {
                _endSession = Game; _endCombat = Game.Combat; _endSaveSeed = Game.LoadedSaveSeed;
                _endState = Game.RealTimeCombat;
                _endPeers = GetSyncedPlayers().Where(player => player.Id != Game.LocalPlayerId)
                    .Select(player => player.Id).ToArray();
                _endCaptureQueued = false; _endFailed = false; _endFrame = null; _endBarrier = null;
                _endParticipantIds = null;
                _endFinalState = null;
                _endNativeReconstructionQueued = false;
                _endDeadline = DateTime.UtcNow.AddSeconds(30);
            }
            Game.RealTimeCombat.CombatEndPending = true;
            // Do not capture inside the death callback: SetLifeState and combat
            // membership can still be mutating until this stack has returned.
            return true;
        }

        private bool IsCurrentEnd() => ReferenceEquals(Game, _endSession)
            && ReferenceEquals(Game?.Combat, _endCombat) && _endCombat != null
            && ReferenceEquals(Game.RealTimeCombat, _endState)
            && Game.LoadedSaveSeed == _endSaveSeed && Game.RealTimeCombat.CombatEndPending;

        private void TickRealTimeCombatEnd()
        {
            if (!RealTimePresentationRuntime.Enabled || !IsCurrentEnd()) return;
            if (Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable)
            {
                // The otherwise-unused Commit stage owns a native reconstruction
                // barrier. Its own transport has bounded deadlines; do not
                // retransmit Commit or expire the outer checkpoint while a new
                // client world is being loaded and certified.
                if (_endNativeReconstructionQueued
                    && Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Repairing) return;
                if (_endFrame?.Stage == (int)RealTimeCombatEndStage.Finalize
                    && Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Repairing)
                {
                    if (_endFailed)
                    {
                        if (DateTime.UtcNow >= _endNextSend) SendCombatEndFrame();
                        return;
                    }
                    if (DateTime.UtcNow >= _endDeadline) { FailCombatEnd("finalize-timeout"); return; }
                    if (DateTime.UtcNow >= _endNextSend) SendCombatEndFrame();
                    return;
                }
                _endDeadline = DateTime.UtcNow.AddSeconds(30);
                return;
            }
            if (_endFailed)
            {
                if (_endFrame != null && DateTime.UtcNow >= _endNextSend) SendCombatEndFrame();
                return;
            }
            if (DateTime.UtcNow >= _endDeadline)
            {
                FailCombatEnd("final-checkpoint-timeout");
                return;
            }
            if (_endFrame != null)
            {
                if (DateTime.UtcNow >= _endNextSend) SendCombatEndFrame();
                return;
            }
            if (_endCaptureQueued) return;
            _endCaptureQueued = true;
            _mainThreadAccessor.Post(async () =>
            {
                try
                {
                    if (!IsCurrentEnd()) return;
                    if (CombatInteraction.HasActiveRealTimeProjectiles() || CombatInteraction.HasActiveRealTimeAtomicActions())
                    { _endCaptureQueued = false; return; }
                    var finalState = await CombatInteraction.GetRealTimeCombatStateAsync();
                    if (!IsCurrentEnd()) return;
                    if (finalState?.Units == null || finalState.Units.Any(unit => unit?.Descriptor?.Vitals == null))
                        throw new InvalidOperationException("combat-end-invalid-final-unit-state");
                    if (finalState.Units.Any(unit => unit.Descriptor.Vitals.IsInCombat))
                    {
                        // OnCombatEnd can run before the engine's last unit-membership writes. Pausing
                        // here would freeze those writes forever and turn a transient state into a
                        // permanent multiplayer barrier. Leave the world running and retry on a later
                        // main-thread tick; the existing 30-second deadline remains the hard failure.
                        _endCaptureQueued = false;
                        GameInteraction.SetPause(Game.RealTimeCombat.IsManualPauseActive || Game.ForcedPause != null);
                        Logger.LogDebug("Final combat checkpoint is waiting for native unit membership to settle.");
                        return;
                    }
                    var participantIds = finalState.Units.Select(unit => unit.Id).ToArray();
                    if (!GameInteraction.IsWorldPaused)
                    {
                        // Owlcat's IsPaused setter arms the pause for the next Unity tick.
                        // Capturing the native reference in this same callback therefore sees
                        // an unpaused world and is correctly rejected. Request the pause now,
                        // then let the regular combat-end tick retry the capture after the game
                        // has actually frozen its world.
                        GameInteraction.SetPause(true);
                        if (!GameInteraction.IsWorldPaused)
                        {
                            _endCaptureQueued = false;
                            return;
                        }
                    }
                    // The native reconstruction intentionally projects ability contexts to the
                    // stable source blueprint that survives an Owlcat save/load. Capture the final
                    // frame through that exact projection as well. Sending the ordinary RT snapshot
                    // here made the later native digest differ even when both worlds were mechanically
                    // identical (a long-lived buff could keep AbilityData only in the live world).
                    finalState = CombatInteraction.GetRealTimeNativeReferenceState(
                        retainHistoricalCombatUnits: true);
                    CombatInteractionService.RetainNativeCombatUnits(finalState, participantIds);
                    // These fields certify the archive itself and are not part of the final gameplay
                    // frame applied before the native reconstruction barrier.
                    finalState.NativeTopology = null;
                    finalState.NativeUnits = null;
                    finalState.AcceptedOrderReference = null;
                    if (!IsCurrentEnd()) return;
                    if (finalState?.Units == null || finalState.Units.Any(unit => unit?.Descriptor?.Vitals == null))
                        throw new InvalidOperationException("combat-end-invalid-final-unit-state");
                    if (finalState.Units.Any(unit => unit.Descriptor.Vitals.IsInCombat))
                    {
                        _endCaptureQueued = false;
                        GameInteraction.SetPause(Game.RealTimeCombat.IsManualPauseActive || Game.ForcedPause != null);
                        return;
                    }
                    var state = Game.RealTimeCombat;
                    var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = finalState });
                    _endParticipantIds = participantIds;
                    _endFinalState = finalState;
                    if (_endPeers.Length == 0)
                    {
                        // Only for an initially solo host, never drop required peers.
                        if (GetSyncedPlayers().Any(player => player.Id != Game.LocalPlayerId))
                            throw new InvalidOperationException("combat-end-peer-joined-during-solo-capture");
                        state.CanonicalStateHash = hash;
                        CompleteCombatEnd();
                        state.CombatEndPending = false;
                        GameInteraction.SetPause(state.IsManualPauseActive || Game.ForcedPause != null);
                        Logger.LogInformation("[CombatMode] Solo host combat end verified locally. Hash={Hash}", hash);
                        return;
                    }
                    state.Epoch++; state.LastAuthoritativeSequence = 0;
                    state.CanonicalStateHash = hash;
                    _endBarrier = new RealTimeCombatEndBarrier(_endPeers, hash);
                    _endFrame = new NotifyRealTimeCombatEnd
                    {
                        SessionId = Game.Id, LoadedSaveSeed = _endSaveSeed, Epoch = state.Epoch,
                        EndId = Guid.NewGuid().ToString("N"), Stage = (int)RealTimeCombatEndStage.Prepare,
                        CanonicalStateHash = hash,
                        State = Mapper.Map<Networking.Messages.Contracts.NetworkCombatState>(finalState)
                    };
                    _endDeadline = DateTime.UtcNow.AddSeconds(30);
                    SendCombatEndFrame();
                }
                catch (Exception failure)
                {
                    if (IsCurrentEnd())
                    {
                        Logger.LogError(failure, "Unable to capture the final host combat checkpoint.");
                        FailCombatEnd("final-checkpoint-capture-failed");
                    }
                }
            });
        }

        private void SendCombatEndFrame()
        {
            _endNextSend = DateTime.UtcNow.AddSeconds(_endFailed ? 5 : 2);
            Send(_endFrame);
        }

        private void OnClientRealTimeCombatEndAck(long receivedFrom, ClientRealTimeCombatEndAck ack)
        {
            if (ack == null) return;
            var copied = ProtoBuf.Serializer.DeepClone(ack);
            _mainThreadAccessor.Post(() => ApplyCombatEndAck(receivedFrom, copied));
        }

        private void ApplyCombatEndAck(long receivedFrom, ClientRealTimeCombatEndAck ack)
        {
            if (!RealTimePresentationRuntime.Enabled || !IsCurrentEnd() || _endFrame == null || _endBarrier == null
                || ack == null || ack.SessionId != _endFrame.SessionId || ack.LoadedSaveSeed != _endSaveSeed
                || ack.Epoch != _endFrame.Epoch || ack.EndId != _endFrame.EndId) return;
            if (!ack.Applied)
            {
                Logger.LogWarning("Peer rejected the final combat checkpoint. PlayerId={PlayerId}, Reason={Reason}",
                    receivedFrom, ack.Reason?.Substring(0, Math.Min(200, ack.Reason.Length)));
                return; // Retransmit the same immutable reference, never unblock saving.
            }
            if (!_endBarrier.Acknowledge(receivedFrom, ack.Stage, ack.CanonicalStateHash, ack.Applied)) return;
            _endFailed = false;
            if (_endBarrier.Complete)
            {
                var state = Game.RealTimeCombat;
                state.Phase = CombatSynchronizationPhase.Stable;
                state.RecoveryStatus = null;
                CompleteCombatEnd();
                state.CombatEndPending = false;
                GameInteraction.SetPause(state.IsManualPauseActive || Game.ForcedPause != null);
                Logger.LogInformation("Final combat checkpoint confirmed by all peers. EndId={EndId}, Hash={Hash}", _endFrame.EndId, _endFrame.CanonicalStateHash);
                return;
            }
            if (ack.Stage == (int)RealTimeCombatEndStage.Commit
                && _endBarrier.Stage == RealTimeCombatEndStage.Finalize
                && Game.RealTimeCombat.Mode == CombatMode.RealTimeWithPause)
            {
                Game.RealTimeCombat.Phase = CombatSynchronizationPhase.Repairing;
                Game.RealTimeCombat.RecoveryStatus = "rebuilding";
                _endNativeReconstructionQueued = true;
                _endDeadline = DateTime.UtcNow.AddMinutes(5);
                _ = RunCombatEndNativeReconstructionAsync(_endFrame.EndId, _endFrame.Epoch,
                    _endFrame.CanonicalStateHash);
                return;
            }
            _endFrame = new NotifyRealTimeCombatEnd
            {
                SessionId = _endFrame.SessionId, LoadedSaveSeed = _endSaveSeed, Epoch = _endFrame.Epoch,
                EndId = _endFrame.EndId, Stage = (int)_endBarrier.Stage, CanonicalStateHash = _endFrame.CanonicalStateHash
            };
            _endDeadline = DateTime.UtcNow.AddSeconds(30);
            SendCombatEndFrame();
        }

        private bool OwnsCombatEndNativeReconstruction(string endId, long epoch)
            => _endNativeReconstructionQueued && IsCurrentEnd() && !_endFailed
            && _endBarrier?.Stage == RealTimeCombatEndStage.Finalize
            && _endFrame?.EndId == endId && _endFrame.Epoch == epoch
            && Game.RealTimeCombat.Epoch == epoch
            && Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Repairing
            && Game.RealTimeCombat.Mode == CombatMode.RealTimeWithPause;

        private async Task RunCombatEndNativeReconstructionAsync(string endId, long epoch, ulong finalHash)
        {
            bool Owned() { lock (ActionLock) return OwnsCombatEndNativeReconstruction(endId, epoch); }
            try
            {
                var manifest = await ReconstructCurrentNativeCheckpointAsync(Owned,
                    _endParticipantIds);
                if (!Owned())
                    throw new InvalidOperationException("combat-end-native-reconstruction-ownership-lost");
                if (manifest == null)
                    throw new InvalidOperationException("combat-end-native-reconstruction-missing-manifest");
                if (manifest.CanonicalStateHash != finalHash)
                {
                    NetworkCombatState nativeReference;
                    lock (ActionLock) nativeReference = _nativeTransfer?.Reference;
                    throw new InvalidOperationException("combat-end-native-reconstruction-hash-mismatch:"
                        + $"{finalHash:X16}->{manifest.CanonicalStateHash:X16};"
                        + $"temporal={RealTimeCombatTemporalDiff.Summarize(_endFinalState, nativeReference) ?? "none"};"
                        + $"state={RealTimeCombatDiff.Summarize(_endFinalState, nativeReference)}");
                }
                if (!await CommitCurrentNativeCheckpointAsync(manifest, Owned))
                    throw new InvalidOperationException("combat-end-native-reconstruction-commit-refused");
                _mainThreadAccessor.Post(() =>
                {
                    lock (ActionLock)
                    {
                        if (!Owned()) return;
                        ResetNativeCheckpointTransfer();
                        Game.RealTimeCombat.CanonicalStateHash = finalHash;
                        Game.RealTimeCombat.RecoveryStatus = "committed";
                        _endNativeReconstructionQueued = false;
                        _endFrame = new NotifyRealTimeCombatEnd
                        {
                            SessionId = Game.Id, LoadedSaveSeed = _endSaveSeed, Epoch = epoch,
                            EndId = endId, Stage = (int)RealTimeCombatEndStage.Finalize,
                            CanonicalStateHash = finalHash
                        };
                        _endDeadline = DateTime.UtcNow.AddSeconds(30);
                        SendCombatEndFrame();
                    }
                });
            }
            catch (Exception failure)
            {
                Logger.LogError(failure,
                    "Final combat checkpoint native reconstruction failed. EndId={EndId}", endId);
                if (Owned() && _nativeTransfer != null) await AbortCurrentNativeCheckpointAsync();
                _mainThreadAccessor.Post(() =>
                {
                    lock (ActionLock)
                    {
                        if (!IsCurrentEnd() || _endFrame?.EndId != endId || _endFrame.Epoch != epoch) return;
                        _endNativeReconstructionQueued = false;
                        Game.RealTimeCombat.RecoveryStatus = "stopped";
                        Game.RealTimeCombat.RecoveryFailure ??= "combat-end-native-reconstruction-not-committed";
                        FailCombatEnd("final-native-reconstruction-failed");
                    }
                });
            }
        }

        private void FailCombatEnd(string reason)
        {
            _endFailed = true;
            GameInteraction.SetPause(true);
            Logger.LogError("Combat end remains protected; saving is blocked. Reason={Reason}, EndId={EndId}", reason, _endFrame?.EndId);
            PlayerNotification.ShowWarningNotification(WellKnownKeys.GameNotifications.Combat.FinalSyncFailed.Key);
        }

        private bool RetryRealTimeCombatEnd()
        {
            if (!IsCurrentEnd()) return false;
            if (_endFailed && _endFrame == null)
            {
                // A failure before publication has no immutable peer checkpoint to
                // preserve. A manual synchronization request may safely retry the
                // host capture against the same session, combat and loaded world.
                _endFailed = false;
                _endCaptureQueued = false;
                Game.RealTimeCombat.RecoveryStatus = "preparing";
                Game.RealTimeCombat.RecoveryFailure = null;
                _endDeadline = DateTime.UtcNow.AddSeconds(30);
            }
            TickRealTimeCombatEnd();
            return true;
        }
    }
}
