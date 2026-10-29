using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
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
        private static bool SavingBlocked => Services.CombatSaving.CombatSavePauseLease.Active && !Services.CombatSaving.CombatSaveNativePermission.IsChecking || Services.CombatSaving.CombatSaveLoadState.Active || SessionRejoinRuntime.Blocked || RealTimeNativeLoadLease.IsActive || Main.Multiplayer?.IsSavingBlockedByCombatSync == true
            || RealTimePresentationRuntime.Enabled
                && (RealTimeBuffReplicaStore.RequiresMechanicalRebuild(Game.Instance?.State?.Units)
                    || RealTimeAreaReplicaFactory.RequiresMechanicalRebuild(Game.Instance?.State?.AreaEffects)
                    || RealTimeNativeGraphRepair.RequiresRebuild(Game.Instance?.State));

        [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.IsSaveAllowed))]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> SaveManager_IsSaveAllowed_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var combatGetter = AccessTools.PropertyGetter(typeof(Player), nameof(Player.IsInCombat));
            // Unsupported native layout stays locked; never replace a whole result.
            if (code.Count(i => i.Calls(combatGetter)) != 1) return code;
            var call = code.Single(i => i.Calls(combatGetter));
            call.opcode = OpCodes.Call;
            call.operand = AccessTools.Method(typeof(Services.CombatSaving.CombatSaveNativePermission), "IsBlockingCombat");
            return code;
        }

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
