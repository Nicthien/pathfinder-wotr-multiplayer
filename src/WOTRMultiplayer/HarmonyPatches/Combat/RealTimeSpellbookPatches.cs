using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public static class RealTimeSpellbookPatches
    {
        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.DeclaredMethod(typeof(Spellbook), "Spend",
                new[] { typeof(AbilityData), typeof(bool) })
                ?? throw new MissingMethodException("Spellbook.Spend");
            yield return AccessTools.DeclaredMethod(typeof(Spellbook), "RestoreSpontaneousSlots",
                new[] { typeof(int), typeof(int) })
                ?? throw new MissingMethodException("Spellbook.RestoreSpontaneousSlots");
            yield return AccessTools.DeclaredMethod(typeof(SpellSlot), "Spend", Type.EmptyTypes)
                ?? throw new MissingMethodException("SpellSlot.Spend");
        }

        [HarmonyPrefix]
        public static bool BeforeMutation(MethodBase __originalMethod)
        {
            if (!RealTimePresentationRuntime.IsClientActive) return true;
            Main.Multiplayer.ReportRealTimeAIActionFault(null, null,
                "autonomous-client-spell-slot:" + __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name);
            return false;
        }
    }

    [HarmonyPatch(typeof(Spellbook), "SpendInternal")]
    public static class RealTimeSpellbookInternalSpendPatch
    {
        [HarmonyPrefix]
        public static bool BeforeSpend(bool doSpend)
        {
            // CanSpend uses this same helper with doSpend=false. Keep read-only
            // availability queries alive for targeting and the action bar.
            if (!doSpend || !RealTimePresentationRuntime.IsClientActive) return true;
            Main.Multiplayer.ReportRealTimeAIActionFault(null, null,
                "autonomous-client-spell-slot:Spellbook.SpendInternal");
            return false;
        }
    }
}
