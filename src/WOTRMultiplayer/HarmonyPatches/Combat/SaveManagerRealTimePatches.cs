using System.Collections.Generic;
using HarmonyLib;
using Kingmaker;
using Kingmaker.EntitySystem.Persistence;
using WOTRMultiplayer.Localization;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public static class SaveManagerRealTimePatches
    {
        private static bool SavingBlocked => Main.Multiplayer?.IsSavingBlockedByCombatSync == true
            || RealTimePresentationRuntime.Enabled
                && (RealTimeBuffReplicaStore.RequiresMechanicalRebuild(Game.Instance?.State?.Units)
                    || RealTimeAreaReplicaFactory.RequiresMechanicalRebuild(Game.Instance?.State?.AreaEffects));

        [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.IsSaveAllowed))]
        [HarmonyPostfix]
        public static void SaveManager_IsSaveAllowed_Postfix(ref bool __result)
        {
            if (SavingBlocked)
            {
                __result = false;
            }
        }

        [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRoutine))]
        [HarmonyPrefix]
        public static bool SaveManager_SaveRoutine_Prefix(ref IEnumerator<object> __result)
        {
            if (!SavingBlocked)
            {
                return true;
            }

            var messageKey = Main.Multiplayer?.IsSavingBlockedAfterRealTimeDisconnect == true
                ? WellKnownKeys.GameNotifications.Combat.SaveBlockedAfterDisconnect.Key
                : WellKnownKeys.GameNotifications.Combat.SaveBlockedDuringSynchronization.Key;
            Main.PlayerNotification?.ShowWarningNotification(messageKey);
            __result = EmptyRoutine().GetEnumerator();
            return false;
        }

        private static IEnumerable<object> EmptyRoutine()
        {
            yield break;
        }
    }
}
