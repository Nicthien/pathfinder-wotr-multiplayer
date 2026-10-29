namespace WOTRMultiplayer.Services.CombatSaving
{
    internal static class CombatSaveMessages
    {
        internal static string ForResult(string result) => result switch
        {
            "saved" => WellKnownKeys.LobbyWindow.RealTime.SaveCombatSaved.Key,
            "recovery-started" => WellKnownKeys.LobbyWindow.RealTime.SaveCombatRecovering.Key,
            "busy" => WellKnownKeys.LobbyWindow.RealTime.SaveCombatBusy.Key,
            "action-in-progress" => WellKnownKeys.LobbyWindow.RealTime.SaveCombatWaitAction.Key,
            "player-turn-required" or "turn-based-combat-required" or "not-playing" => WellKnownKeys.LobbyWindow.RealTime.SaveCombatWaitTurn.Key,
            "synchronization-required" or "divergence" or "invalid-acknowledgements" or "invalid-digest" => WellKnownKeys.LobbyWindow.RealTime.SaveCombatSync.Key,
            "timeout" => WellKnownKeys.LobbyWindow.RealTime.SaveCombatTimeout.Key,
            "peer-refused" => WellKnownKeys.LobbyWindow.RealTime.SaveCombatPeerRefused.Key,
            "session-changed" or "cancelled" => WellKnownKeys.LobbyWindow.RealTime.SaveCombatSessionChanged.Key,
            "native-save-forbidden" or "native-mode-not-ready" or "scripted-sequence" => WellKnownKeys.LobbyWindow.RealTime.SaveCombatNativeRefused.Key,
            "transition-in-progress" => WellKnownKeys.LobbyWindow.RealTime.SaveCombatTransition.Key,
            "restoration-not-validated" => WellKnownKeys.LobbyWindow.RealTime.SaveCombatLocked.Key,
            _ => WellKnownKeys.LobbyWindow.RealTime.SaveCombatFailed.Key
        };
    }
}
