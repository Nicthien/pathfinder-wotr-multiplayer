using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.View.MapObjects;
using Kingmaker.View;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public static class RealTimeAreaVisualLifecyclePatches
    {
        [HarmonyPrepare]
        public static bool Prepare() => RealTimePresentationRuntime.Enabled;

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> Targets()
        {
            foreach (var name in new[] { "OnEnable", "OnDisable", "OnDestroy", "CreateEntityData", "SpawnFxs" })
                yield return AccessTools.DeclaredMethod(typeof(AreaEffectView), name)
                    ?? throw new MissingMethodException(typeof(AreaEffectView).FullName, name);
            yield return AccessTools.DeclaredMethod(typeof(EntityViewBase), "UpdateViewActive")
                ?? throw new MissingMethodException(typeof(EntityViewBase).FullName, "UpdateViewActive");
        }

        [HarmonyPrefix]
        public static bool BeforeNativeLifecycle(EntityViewBase __instance, MethodBase __originalMethod)
        {
            if (__instance is not AreaEffectView view || !RealTimeAreaVisual.IsOwned(view)) return true;
            if (__originalMethod.Name == "OnDestroy") RealTimeAreaVisual.For(view)?.Dispose();
            return false;
        }
    }
}
