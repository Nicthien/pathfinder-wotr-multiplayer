using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Kingmaker;
using Kingmaker.EntitySystem.Persistence;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public static class SaveManagerRealTimePatches
    {
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

    }
}
