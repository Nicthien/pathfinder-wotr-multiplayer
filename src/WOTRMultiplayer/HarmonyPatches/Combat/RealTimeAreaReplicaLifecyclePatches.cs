using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public static class RealTimeAreaReplicaLifecyclePatches
    {
        [HarmonyPrepare]
        public static bool Prepare() => RealTimePresentationRuntime.Enabled;

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> Targets()
        {
            foreach (var name in new[] { "Tick", "TickRound", "HandleTimeChanged", "HandleEnd", "UpdateUnits", "UpdateUnit",
                "OnPostLoad", "OnApplyPostLoadFixes", "OnDispose", "OnIsInGameChanged", "OnAreaPartUnload", "CreateViewForData" })
                yield return AccessTools.DeclaredMethod(typeof(AreaEffectEntityData), name)
                    ?? throw new MissingMethodException(typeof(AreaEffectEntityData).FullName, name);
        }

        // Keep presentation data inert after disconnect or mode changes too.
        // Its renderer/transaction must clean up explicitly, not HandleEnd.
        [HarmonyPrefix]
        public static bool BeforeNativeMutation(AreaEffectEntityData __instance) =>
            !RealTimeAreaReplicaFactory.IsReplica(__instance);
    }
}
