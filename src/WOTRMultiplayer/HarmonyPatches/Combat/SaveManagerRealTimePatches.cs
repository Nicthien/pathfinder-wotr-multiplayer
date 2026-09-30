using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.EntitySystem.Persistence;
using WOTRMultiplayer.Localization;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public static class SaveManagerRealTimePatches
    {
        [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.IsSaveAllowed))]
        [HarmonyPostfix]
        public static void SaveManager_IsSaveAllowed_Postfix(ref bool __result)
        {
            if (Main.Multiplayer?.IsSavingBlockedByCombatSync == true)
            {
                __result = false;
            }
        }

        [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRoutine))]
        [HarmonyPrefix]
        public static bool SaveManager_SaveRoutine_Prefix(ref IEnumerator<object> __result)
        {
            if (Main.Multiplayer?.IsSavingBlockedByCombatSync != true)
            {
                return true;
            }

            Main.PlayerNotification?.ShowWarningNotification(WellKnownKeys.GameNotifications.Combat.SaveBlockedDuringSynchronization.Key);
            __result = EmptyRoutine().GetEnumerator();
            return false;
        }

        private static IEnumerable<object> EmptyRoutine()
        {
            yield break;
        }
    }
}
