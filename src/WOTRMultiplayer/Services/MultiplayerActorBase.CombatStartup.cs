using System;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;

namespace WOTRMultiplayer.Services
{
    public abstract partial class MultiplayerActorBase
    {
        private NetworkGame _startupFailureSession;
        private int _startupFailureSave;
        protected NetworkGame NativeCombatRestartSession;
        protected int NativeCombatRestartSave;
        protected bool IsNativeCombatRestart => Game != null && ReferenceEquals(Game, NativeCombatRestartSession)
            && Game.LoadedSaveSeed == NativeCombatRestartSave;

        internal bool HasCombatStartupFailure => Game != null && ReferenceEquals(Game, _startupFailureSession)
            && Game.LoadedSaveSeed == _startupFailureSave;

        internal bool IsCombatStartupBlocked => HasCombatStartupFailure || IsNativeCombatRestart
            || Game?.Combat != null && (Game.Combat.Stage != NetworkCombatStage.Running
                || Game.Combat.RealTimeBootstrapPending);

        internal bool HoldsCombatMembership => IsCombatStartupBlocked
            || Game?.Combat?.Stage == NetworkCombatStage.Running
                && Game.RealTimeCombat?.Mode == CombatMode.TurnBased
                && (Game.Combat.Turn == null || Game.Combat.Turn.Stage == NetworkCombatTurnStage.Starting
                    || Game.Combat.Turn.Stage == NetworkCombatTurnStage.Initialization);

        internal void FailCombatStartup(string reason)
        {
            if (Game == null) return;
            if (!HasCombatStartupFailure)
            {
                Logger.LogError("[CombatStartup] Protected stop; reload a safe save. Reason={Reason}", reason);
                PlayerNotification.ShowWarningNotification(WellKnownKeys.LobbyWindow.RealTime.RecoveryStopped.Key);
            }
            _startupFailureSession = Game;
            _startupFailureSave = Game.LoadedSaveSeed;
            Game.RealTimeCombat.RecoveryStatus = "combat-startup-failed";
            Game.RealTimeCombat.RecoveryFailure = reason;
            GameInteraction.SetPause(true);
        }

        // Called by the menu/network update, including while the world is paused.
        // Native RTwP does not drive the turn-based controller's Finalizing gate.
        internal void TickCombatStartup()
        {
            if (IsSaveLoadPending || CombatSaving.CombatSaveLoadState.Active || Game == null
                || WOTRMultiplayer.Services.GameInteraction.RealTimeNativeLoadLease.SuppressesLifecycle(this)) return;
            if (HasCombatStartupFailure) { GameInteraction.SetPause(true); return; }
            if (IsNativeCombatRestart && Game.Combat == null) return;
            if (Game.Combat == null)
            {
                if (Game.Stage == NetworkLobbyStage.Playing && Game.CurrentArea != null
                    && Game.ForcedPause?.Reason != NetworkForcedPauseReason.AreaLoading && CombatInteraction.IsInCombat())
                    FailCombatStartup("native-combat-without-network-encounter");
                return;
            }
            var combat = Game.Combat;
            if (combat.RealTimeBootstrapPending && Game.RealTimeCombat.Mode == CombatMode.RealTimeWithPause
                && Game.RealTimeCombat.Epoch > 0 && Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Stable)
                combat.RealTimeBootstrapPending = false;
            if (combat.Stage == NetworkCombatStage.Running && !combat.RealTimeBootstrapPending) return;
            if (combat.RealTimeBootstrapPending) GameInteraction.SetPause(true);
            if (combat.StartedAt != default && DateTime.UtcNow - combat.StartedAt > TimeSpan.FromSeconds(30))
            {
                FailCombatStartup("initialization-timeout:" + combat.Stage);
                return;
            }
            if (this is MultiplayerHost host && host.IsActive)
            {
                host.CanInitializeCombat();
                if (ReferenceEquals(Game?.Combat, combat)) host.CanContinueCombat();
            }
            else if (this is MultiplayerClient client && client.IsActive) client.CanInitializeCombat();
        }
    }
}
