using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.Blueprints;
using Kingmaker.UnitLogic;
using WOTRMultiplayer.Services;

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
            Main.Multiplayer.ReportRealTimeAIActionFault(__instance.m_Owner?.Unit?.UniqueId, null,
                "autonomous-client-resource:" + __originalMethod.Name);
            return false;
        }
    }
}
