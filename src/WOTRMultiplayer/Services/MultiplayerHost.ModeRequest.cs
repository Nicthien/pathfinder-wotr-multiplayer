using System;
using System.Threading.Tasks;
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
        private bool _nativeReconstructionRequiredForModeSwitch;

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

         private void TickRequestedCombatMode()
            => SafeFireAndForget(() => TickRequestedCombatModeAsync());

         private async Task TickRequestedCombatModeAsync()
        {
            if (!_queuedCombatMode.HasValue) return;
            if (!IsActive || !ReferenceEquals(Game, _queuedCombatModeSession)
                || Game.LoadedSaveSeed != _queuedCombatModeSaveSeed)
            {
                _queuedCombatMode = null;
                _nativeReconstructionRequiredForModeSwitch = false;
                return; // Never apply an old request after loading or reconnecting.
            }
            if (_nativeReconstructionRequiredForModeSwitch) return;
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

        private async Task ContinueCombatModeAfterNativeReconstructionAsync()
        {
            if (!_nativeReconstructionRequiredForModeSwitch || !_queuedCombatMode.HasValue)
                return;
            var mode = _queuedCombatMode.Value;
            var session = _queuedCombatModeSession;
            var saveSeed = _queuedCombatModeSaveSeed;
            _nativeReconstructionRequiredForModeSwitch = false;
            _queuedCombatMode = null;
            if (!IsActive || !ReferenceEquals(Game, session) || Game.LoadedSaveSeed != saveSeed
                || Game?.Combat?.Stage != NetworkCombatStage.Running
                || Game.RealTimeCombat?.Phase != CombatSynchronizationPhase.Stable
                || Game.RealTimeCombat.Mode == mode)
                return;
            try
            {
                Logger.LogInformation("[CombatMode] Native reconstruction verified; continuing retained mode request. Mode={Mode}", mode);
                if (!await RequestRealTimeCombatModeAsync(mode))
                    PlayerNotification.ShowWarningNotification(WellKnownKeys.LobbyWindow.RealTime.ModeUnavailable.Key);
            }
            catch (Exception failure)
            {
                Logger.LogError(failure, "[CombatMode] Mode request failed after native reconstruction.");
                PlayerNotification.ShowWarningNotification(WellKnownKeys.LobbyWindow.RealTime.ModeUnavailable.Key);
            }
        }
    }
}
