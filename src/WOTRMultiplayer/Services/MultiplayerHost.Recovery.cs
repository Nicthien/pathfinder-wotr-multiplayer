using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Microsoft.Extensions.Logging;
using ProtoBuf;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerHost
    {
        private object _nativeRecoveryOwner;
        internal string LastNativeRecoveryFailure { get; private set; }
        private bool _waitingForRealTimePeer;
        private readonly Dictionary<string, NotifyRealTimeAuthoritativeEvent> _activePresentationActions = new(StringComparer.Ordinal);
        private object _actionSession;
        private object _actionCombat;

        private void RememberPresentationAction(NotifyRealTimeAuthoritativeEvent frame)
        {
            if (!ReferenceEquals(_actionSession, Game) || !ReferenceEquals(_actionCombat, Game?.Combat))
            {
                _activePresentationActions.Clear(); _actionSession = Game; _actionCombat = Game?.Combat;
            }
            var stage = frame.Attack?.Stage ?? frame.Ability?.Stage;
            if (string.IsNullOrWhiteSpace(frame.UnitId) || stage == null) return;
            if (stage == (int)RealTimePresentationStage.Start)
                _activePresentationActions[frame.UnitId] = Serializer.DeepClone(frame);
            else if (stage == (int)RealTimePresentationStage.End || stage == (int)RealTimePresentationStage.Interrupt)
                if (_activePresentationActions.TryGetValue(frame.UnitId, out var active) && active.ActionId == frame.ActionId)
                    _activePresentationActions.Remove(frame.UnitId);
        }

        private void RepublishActivePresentationActions()
        {
            if (!RealTimePresentationRuntime.Enabled || !ReferenceEquals(_actionSession, Game)
                || !ReferenceEquals(_actionCombat, Game?.Combat)) return;
            if (!CombatInteraction.HasActiveRealTimeAtomicActions()) { _activePresentationActions.Clear(); return; }
            // Presentation only: never re-submit the accepted gameplay command.
            foreach (var original in _activePresentationActions.Values.ToArray())
            {
                var current = original.Ability != null
                    ? HarmonyPatches.Combat.RealTimeAbilityPresentationPatches.IsCurrentAction(original.UnitId, original.ActionId)
                    : HarmonyPatches.Combat.RealTimePresentationAttackPatches.IsCurrentAction(original.UnitId, original.ActionId);
                if (!current) { _activePresentationActions.Remove(original.UnitId); continue; }
                var frame = Serializer.DeepClone(original);
                if (frame.Ability != null)
                {
                    var elapsed = Math.Max(0, (Kingmaker.Game.Instance.TimeController.GameTime.Ticks - frame.HostGameTimeTicks)
                        / (double)TimeSpan.TicksPerSecond);
                    frame.Ability.CastingTime = Math.Max(0, frame.Ability.CastingTime - (float)elapsed);
                }
                frame.HostGameTimeTicks = Kingmaker.Game.Instance.TimeController.GameTime.Ticks;
                PublishRealTimeAuthoritativeEvent(frame);
            }
        }

        private async Task RunNativeRecoveryAsync()
        {
            if (_nativeRecoveryOwner != null || _waitingForRealTimePeer) return;
            var owner = new object(); _nativeRecoveryOwner = owner;
            var session = Game; var state = session?.RealTimeCombat; var epoch = state?.Epoch;
            bool Owned() => ReferenceEquals(_nativeRecoveryOwner, owner) && ReferenceEquals(Game, session)
                && ReferenceEquals(session?.RealTimeCombat, state) && state?.Epoch == epoch
                && state.Phase == CombatSynchronizationPhase.Repairing && !_waitingForRealTimePeer;
            try
            {
                if (!Owned()) return;
                LastNativeRecoveryFailure = null;
                state.RecoveryStatus = "rebuilding"; state.RecoveryFailure = null;
                var manifest = await ReconstructCurrentNativeCheckpointAsync(Owned);
                if (!Owned()) return;
                if (manifest == null || !await CommitCurrentNativeCheckpointAsync(manifest, Owned))
                    throw new InvalidOperationException("native-reconstruction-not-committed");
                _mainThreadAccessor.Post(() =>
                {
                    lock (ActionLock)
                    {
                        if (!Owned()) return;
                        // A real native repair can follow an Owlcat world replacement, so
                        // refresh the host-side encounter registry with entities from the
                        // current world before Resume. Synthetic protocol tests replace only
                        // the native archive boundary and deliberately have no Unity world.
                        if (NativeCheckpointCapture == null)
                        {
                            var reference = _nativeTransfer?.Reference
                                ?? throw new InvalidOperationException("native-recovery-reference-missing");
                            var participants = RealTimeEncounterParticipants<UnitEntityData>.For(Game?.Combat)
                                ?? throw new InvalidOperationException("native-recovery-encounter-missing");
                            var nativeGame = Kingmaker.Game.Instance
                                ?? throw new InvalidOperationException("native-recovery-world-missing");
                            var nativeUnits = nativeGame.State.Units.All.ToDictionary(
                                unit => unit.UniqueId, StringComparer.Ordinal);
                            using (var rebound = participants.RebindCheckpoint(
                                       reference.Units.Select(unit => unit?.Id),
                                       id => nativeUnits.TryGetValue(id, out var unit) ? unit : null,
                                       Owned))
                                rebound.Commit();
                        }
                        state.CanonicalStateHash = manifest.CanonicalStateHash;
                        state.RecoveryStatus = "committed";
                        _realTimeCriticalFaulted = false;
                        ResetNativeCheckpointTransfer();
                        CompleteRealTimeRepair();
                    }
                });
            }
            catch (Exception error)
            {
                LastNativeRecoveryFailure = error.ToString();
                Logger.LogError(error,
                    "Authoritative reconstruction failed. Epoch={Epoch}; attempting verified turn-based fallback.", epoch);
                var transferExisted = Owned() && _nativeTransfer != null;
                var transferRevoked = !transferExisted || await AbortCurrentNativeCheckpointAsync();
                _mainThreadAccessor.Post(() =>
                {
                    lock (ActionLock)
                    {
                        if (!Owned()) return;
                        if (!transferRevoked)
                        {
                            // A client may still be loading the native archive.  In
                            // that one case it is unsafe to start another mechanical
                            // transition; remain paused and keep saving blocked.
                            state.RecoveryStatus = "stopped";
                            state.RecoveryFailure = "native-transfer-abort-not-acknowledged";
                            _nativeReconstructionRequiredForModeSwitch = false;
                            _queuedCombatMode = null;
                            _realTimeRepairDeadline = default;
                            GameInteraction.SetPause(true);
                            Send(new NotifyRealTimeRepair { Epoch = state.Epoch, Stage = (int)RealTimeRepairStage.Stopped,
                                SessionId = session.Id, LoadedSaveSeed = session.LoadedSaveSeed,
                                Mode = (int)state.Mode, Reason = state.RecoveryFailure });
                            return;
                        }
                        if (transferExisted) ResetNativeCheckpointTransfer();
                        state.RecoveryStatus = "falling-back";
                        state.RecoveryFailure = "native-reconstruction-not-committed";
                        _nativeReconstructionRequiredForModeSwitch = false;
                        _queuedCombatMode = null;
                        _realTimeRepairDeadline = default;
                        GameInteraction.SetPause(true);
                        new RealTimeCombatCoordinator(state).ForceFallbackAfterUntrustedRuleResult();
                        _nativeRecoveryOwner = null;
                        _ = StartRealTimeRepairFallbackAsync();
                    }
                });
            }
            finally
            {
                // Main-thread posts above own completion. Release ownership only
                // after they execute, not on an arbitrary task continuation.
                _mainThreadAccessor.Post(() => { lock (ActionLock)
                    if (ReferenceEquals(_nativeRecoveryOwner, owner)) _nativeRecoveryOwner = null; });
            }
        }

        private void TickRealTimeReconnection()
        {
            if (!_waitingForRealTimePeer || !IsRealTimeCombatActive) return;
            GameInteraction.SetPause(true);
            if (!GetSyncedPlayers().Any(player => player.Id != Game.LocalPlayerId) || _nativeRecoveryOwner != null) return;
            if (Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable
                && Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Repairing) return;
            _waitingForRealTimePeer = false;
            _realTimeCriticalFaulted = true; // A reconnect always requires a fresh native world.
            ResetNativeCheckpointTransfer();
            // BeginRepair increments the epoch: all pre-disconnect commands and
            // native transfer identities become unusable on both peers.
            _ = BeginRealTimeRepairAsync();
        }
    }
}
