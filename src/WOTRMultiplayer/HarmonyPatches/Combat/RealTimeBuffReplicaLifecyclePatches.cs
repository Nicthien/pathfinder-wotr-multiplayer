using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.UnitLogic.Buffs;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public static class RealTimeBuffReplicaLifecyclePatches
    {
        [HarmonyPrepare]
        public static bool Prepare() => RealTimePresentationRuntime.Enabled;

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> Targets()
        {
            foreach (var name in new[] { "TickMechanics", "OnRemove", "Remove", "RemoveRank", "RemoveAfterDelay" })
                yield return AccessTools.DeclaredMethod(typeof(Buff), name)
                    ?? throw new MissingMethodException(typeof(Buff).FullName, name);
        }

        // Raw host replication/rollback never uses these callbacks. Keep
        // replicas inert even after a disconnect changes IsClientActive.
        [HarmonyPrefix]
        public static bool BeforeNativeMutation(Buff __instance) => !RealTimeBuffReplicaStore.IsReplica(__instance);
    }
}
