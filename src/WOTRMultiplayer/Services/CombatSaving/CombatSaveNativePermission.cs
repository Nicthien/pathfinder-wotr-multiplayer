using System;
using Kingmaker;
using Kingmaker.EntitySystem.Persistence;

namespace WOTRMultiplayer.Services.CombatSaving
{
    internal static class CombatSaveNativePermission
    {
        [ThreadStatic] private static bool _checking;
        internal static bool IsChecking => _checking;

        // Synchronous query only: no authorization survives an await or exception.
        // All native restrictions except IsInCombat still execute normally.
        internal static bool Check(MultiplayerHost host)
        {
            if (!CombatSaveActivation.Enabled || _checking || CombatSaveLoadState.Active
                || host?.IsActive != true) return false;
            _checking = true;
            try { return Game.Instance.SaveManager.IsSaveAllowed(SaveInfo.SaveType.Manual); }
            finally { _checking = false; }
        }

        internal static bool IsBlockingCombat(Player player) => !_checking && player.IsInCombat;
    }
}
