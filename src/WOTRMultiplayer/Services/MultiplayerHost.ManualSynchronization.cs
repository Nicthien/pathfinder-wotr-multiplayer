using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerHost
    {
        private DateTime _nextManualSynchronization;
        private NotifyCombatTurnStartSynchronizationRequired _manualTurnStartCheckpoint;
        private NetworkCombatTurn _manualTurnStartOwner;

        // Called from the in-game host UI. Retries actual barriers; never
        // fabricates peer acknowledgements, kills units or changes rewards.
        public async Task<bool> ForceCombatSynchronizationAsync()
        {
            if (CombatSaving.CombatSavePauseLease.Active || CombatSaving.CombatSaveLoadState.Active) return false;
            if (!IsActive || Game?.Combat == null || Game.RealTimeCombat == null
                || _rejoin != null || _nativeRecoveryOwner != null || _waitingForRealTimePeer
                || DateTime.UtcNow < _nextManualSynchronization) return false;
            _nextManualSynchronization = DateTime.UtcNow.AddSeconds(3);
            var state = Game.RealTimeCombat;
            Logger.LogWarning("[ManualSync] Host requested synchronization. Combat={Combat}, Mode={Mode}, Phase={Phase}, Turn={Turn}, Unit={Unit}",
                Game.Combat.Stage, state.Mode, state.Phase, Game.Combat.Turn?.Stage, Game.Combat.Turn?.UnitId);
            try
            {
                if (state.CombatEndPending) { TickRealTimeCombatEnd(); return true; }
                if (state.Phase != CombatSynchronizationPhase.Stable)
                {
                    // A transition owns its checkpoint: do not start a concurrent repair.
                    var barrier = _lastRealTimeModeComplete ?? _lastRealTimeAbort
                        ?? _lastRealTimeModeCommit ?? _lastRealTimeModePrepare;
                    if (barrier != null && barrier.Epoch == state.Epoch && state.RecoveryStatus != "stopped")
                    { Send(barrier); return true; }
                    if (state.Mode != CombatMode.RealTimeWithPause) return false;
                }
                if (state.Mode == CombatMode.RealTimeWithPause)
                {
                    if (state.Phase != CombatSynchronizationPhase.Stable
                        && state.Phase != CombatSynchronizationPhase.Repairing) return false;
                    await BeginRealTimeRepairAsync();
                    return true;
                }
                if (Game.Combat.Stage != NetworkCombatStage.Running) return false;
                var turn = Game.Combat.Turn;
                switch (turn?.Stage)
                {
                    case NetworkCombatTurnStage.Starting:
                        _nextTurnStartRecovery = DateTime.MinValue;
                        TickTurnStartRecovery();
                        return true;
                    case NetworkCombatTurnStage.StartSynchronization:
                        if (!ReferenceEquals(turn, _manualTurnStartOwner) || _manualTurnStartCheckpoint == null) return false;
                        Send(_manualTurnStartCheckpoint);
                        TryStartTurn();
                        return true;
                    case NetworkCombatTurnStage.EndSynchronization:
                        if (_turnEndSynchronizationCheckpoint == null) return false;
                        _turnEndSynchronizationRetries.Clear();
                        Send(_turnEndSynchronizationCheckpoint);
                        TryEndTurn();
                        return true;
                    default:
                        return false; // An active action is not a checkpoint.
                }
            }
            catch (Exception error)
            {
                Logger.LogError(error, "[ManualSync] Synchronization request failed; combat state retained.");
                return false;
            }
        }
    }
}
