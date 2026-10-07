using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public static class RealTimeAreaAuthorityPatches
    {
        [HarmonyPrepare]
        public static bool Prepare() => RealTimePresentationRuntime.Enabled;

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> Targets()
        {
            // Area effects have their own scheduler, independent of unit AI.
            // In particular, UpdateUnits invokes enter/move/exit components
            // directly, without necessarily passing through Rulebook.
            foreach (var name in new[] { "Tick", "TickRound", "HandleTimeChanged", "HandleEnd", "UpdateUnits", "UpdateUnit" })
                yield return AccessTools.DeclaredMethod(typeof(AreaEffectEntityData), name)
                    ?? throw new MissingMethodException(typeof(AreaEffectEntityData).FullName, name);
        }

        [HarmonyPrefix]
        public static bool BeforeSimulation() => !RealTimePresentationRuntime.IsClientActive;
    }

    [HarmonyPatch]
    public static class RealTimeAreaCallbackPatches
    {
        [HarmonyPrepare]
        public static bool Prepare() => RealTimePresentationRuntime.Enabled;

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> Targets()
        {
            // Also cover callbacks reached from spawn, unload and lifecycle
            // paths outside the scheduler. Visual transforms bypass these.
            foreach (var name in new[] { "HandleSpawn", "HandleTick", "HandleRound", "HandleUnitEnter", "HandleUnitMove", "HandleUnitExit" })
                yield return AccessTools.DeclaredMethod(typeof(BlueprintAbilityAreaEffect), name)
                    ?? throw new MissingMethodException(typeof(BlueprintAbilityAreaEffect).FullName, name);
        }

        [HarmonyPrefix]
        public static bool BeforeCallback(MethodBase __originalMethod, AreaEffectEntityData __1)
        {
            if (!RealTimePresentationRuntime.IsClientActive) return true;
            Main.Multiplayer.ReportRealTimeAIActionFault(null, null,
                "autonomous-client-area-callback:" + __originalMethod.Name + ":" + __1?.UniqueId);
            return false;
        }
    }
}
