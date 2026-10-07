using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.UnitLogic.Buffs;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public static class RealTimeBuffAuthorityPatches
    {
        [HarmonyPrepare]
        public static bool Prepare() => RealTimePresentationRuntime.Enabled;

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> Targets()
        {
            // The unit controller's IsInCombat filter does not cover a dead
            // unit or a direct TickAll call. Guard the collection itself too.
            foreach (var name in new[] { "Tick", "Postpone", "UpdateOnDeadOwner" })
                yield return AccessTools.DeclaredMethod(typeof(BuffCollection), name)
                    ?? throw new MissingMethodException(typeof(BuffCollection).FullName, name);
        }

        [HarmonyPrefix]
        public static bool BeforeScheduledMutation() => !RealTimePresentationRuntime.IsClientActive;
    }
}
