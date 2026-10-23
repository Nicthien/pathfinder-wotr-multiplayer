using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.Blueprints;
using Kingmaker.UnitLogic;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public static class RealTimeResourcePatches
    {
        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.DeclaredMethod(typeof(UnitAbilityResourceCollection), "Spend",
                new[] { typeof(BlueprintScriptableObject), typeof(int) })
                ?? throw new MissingMethodException("UnitAbilityResourceCollection.Spend");
            yield return AccessTools.DeclaredMethod(typeof(UnitAbilityResourceCollection), "Restore",
                new[] { typeof(BlueprintScriptableObject), typeof(int), typeof(bool) })
                ?? throw new MissingMethodException("UnitAbilityResourceCollection.Restore");
        }

        [HarmonyPrefix]
        public static bool BeforeMutation(UnitAbilityResourceCollection __instance, MethodBase __originalMethod)
        {
            if (!RealTimePresentationRuntime.IsClientActive) return true;
            // The client still owns native schedulers and animation callbacks,
            // so an attempted mutation is expected even though it must never be
            // authoritative. Because the prefix prevents the write, reporting a
            // repair fault here would turn a successfully blocked callback into
            // a false desynchronization and permanently pause RTwP.
            return false;
        }

        [HarmonyPostfix]
        public static void AfterMutation(UnitAbilityResourceCollection __instance)
        {
            if (!RealTimePresentationRuntime.Enabled) return;
            RealTimeUnitResultCapture.Publish(__instance.m_Owner?.Unit, "resource");
        }
    }
}
