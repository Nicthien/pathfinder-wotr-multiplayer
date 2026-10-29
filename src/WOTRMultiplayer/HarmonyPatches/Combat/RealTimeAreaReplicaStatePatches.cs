using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public static class RealTimeAreaReplicaStatePatches
    {
        [HarmonyPrepare]
        public static bool Prepare() => RealTimePresentationRuntime.Enabled;

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> Targets()
        {
            foreach (var property in new[] { "IsInGame", "Suppressed" })
                yield return AccessTools.PropertySetter(typeof(EntityDataBase), property)
                    ?? throw new MissingMethodException(typeof(EntityDataBase).FullName, "set_" + property);
        }

        // These setters have callbacks even when no combat rule is involved.
        // Explicit raw restoration/pool bookkeeping are the only writers.
        [HarmonyPrefix]
        public static bool BeforeNativeStateChange(EntityDataBase __instance) =>
            __instance is not AreaEffectEntityData area || !RealTimeAreaReplicaFactory.IsReplica(area);
    }
}
