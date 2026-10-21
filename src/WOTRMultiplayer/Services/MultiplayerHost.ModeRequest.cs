using System;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerHost
    {
        private CombatMode? _queuedCombatMode;
        private NetworkGame _queuedCombatModeSession;
        private int _queuedCombatModeSaveSeed;

        private bool HasModeBlockingForcedPause => Game.ForcedPause != null
            && Game.ForcedPause.Reason != NetworkForcedPauseReason.Manual;

        private void QueueRequestedCombatMode(CombatMode mode)
        {
            _queuedCombatMode = mode;
            _queuedCombatModeSession = Game;
            _queuedCombatModeSaveSeed = Game.LoadedSaveSeed;
            Logger.LogInformation("[CombatMode] Hotkey request retained until current synchronization finishes. Mode={Mode}, Phase={Phase}", mode, Game.RealTimeCombat.Phase);
            PlayerNotification.ShowWarningNotification(WellKnownKeys.LobbyWindow.RealTime.ModeQueued.Key);
        }

        private async void TickRequestedCombatMode()
        {
            if (!_queuedCombatMode.HasValue) return;
            if (!IsActive || !ReferenceEquals(Game, _queuedCombatModeSession)
                || Game.LoadedSaveSeed != _queuedCombatModeSaveSeed)
            {
                _queuedCombatMode = null;
                return; // Never apply an old request after loading or reconnecting.
            }
            var state = Game.RealTimeCombat;
            if (state == null || state.Phase != CombatSynchronizationPhase.Stable
                || state.CombatEndPending || (Game.Combat?.Stage == NetworkCombatStage.Running && HasModeBlockingForcedPause)
                || _rejoin != null || _waitingForRealTimePeer) return;
            var mode = _queuedCombatMode.Value;
            _queuedCombatMode = null;
            try
            {
                if (Game.Combat?.Stage != NetworkCombatStage.Running)
                    SetPreferredRealTimeCombatMode(mode);
                else if (state.Mode != mode)
                {
                    Logger.LogInformation("[CombatMode] Applying retained mode request. Mode={Mode}", mode);
                    if (!await RequestRealTimeCombatModeAsync(mode))
                        PlayerNotification.ShowWarningNotification(WellKnownKeys.LobbyWindow.RealTime.ModeUnavailable.Key);
                }
            }
            catch (Exception failure)
            {
                Logger.LogError(failure, "[CombatMode] Retained mode request failed; no local-only switch performed.");
            }
        }
    }
}
