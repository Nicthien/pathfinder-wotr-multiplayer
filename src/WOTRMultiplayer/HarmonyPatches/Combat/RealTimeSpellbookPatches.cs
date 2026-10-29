using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

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
            // A blocked client callback did not mutate state. Host snapshots
            // remain authoritative, so no recovery is required.
            return false;
        }

        [HarmonyPostfix]
        public static void AfterMutation(object __instance)
        {
            if (!RealTimePresentationRuntime.Enabled) return;
            var unit = __instance is Spellbook book ? book.Owner?.Unit
                : (__instance as SpellSlot)?.Spell?.Caster?.Unit;
            RealTimeUnitResultCapture.Publish(unit, "spell-slot");
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
            return false;
        }

        [HarmonyPostfix]
        public static void AfterSpend(Spellbook __instance, bool doSpend)
        {
            if (!RealTimePresentationRuntime.Enabled) return;
            if (doSpend) RealTimeUnitResultCapture.Publish(__instance.Owner?.Unit, "spell-slot");
        }
    }
}
