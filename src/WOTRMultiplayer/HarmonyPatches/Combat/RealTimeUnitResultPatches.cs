using System;
using HarmonyLib;
using Kingmaker.Controllers.Units;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.Enums;
using Kingmaker.UnitLogic;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public static class RealTimeUnitResultPatches
    {
        [HarmonyPrepare]
        public static bool Prepare() => RealTimePresentationRuntime.Enabled;

        public static bool IsVitalStat(ModifiableValue stat) => stat != null
            && (stat.Type == StatType.HitPoints || stat.Type == StatType.TemporaryHitPoints || stat.Type == StatType.DamageNonLethal);

        private static bool RejectClient(UnitEntityData unit, string reason)
        {
            if (!RealTimePresentationRuntime.IsClientActive) return true;
            // The attempted client-side mutation is fully rejected by this
            // prefix. Trigger recovery only for a mutation that escaped a guard
            // or for a host event that cannot be applied.
            return false;
        }

        [HarmonyPatch(typeof(UnitDescriptor), "set_Damage")]
        [HarmonyPrefix]
        public static bool UnitDescriptor_set_Damage_Prefix(UnitDescriptor __instance) => RealTimeNativeLoadLease.IsHydrating || RejectClient(__instance.Unit, "damage");

        [HarmonyPatch(typeof(UnitDescriptor), "set_Damage")]
        [HarmonyPostfix]
        public static void UnitDescriptor_set_Damage_Postfix(UnitDescriptor __instance) => RealTimeUnitResultCapture.Publish(__instance.Unit, "damage");

        [HarmonyPatch(typeof(ModifiableValue), "set_BaseValue")]
        [HarmonyPrefix]
        public static bool ModifiableValue_set_BaseValue_Prefix(ModifiableValue __instance) => RealTimeNativeLoadLease.IsHydrating || ModifiableValue_UpdateValue_Prefix(__instance);

        [HarmonyPatch(typeof(ModifiableValue), "UpdateValue")]
        [HarmonyPrefix]
        public static bool ModifiableValue_UpdateValue_Prefix(ModifiableValue __instance) => __instance == null
            // Hydration restores the serialized field, never its calculated
            // consequences. A later reference snapshot must restore caches.
            || !RealTimeNativeLoadLease.IsHydrating && RejectClient(__instance.Owner?.Unit, "stat:" + __instance.Type);

        [HarmonyPatch(typeof(ModifiableValue), "UpdateValue")]
        [HarmonyPostfix]
        public static void ModifiableValue_UpdateValue_Postfix(ModifiableValue __instance)
        {
            if (__instance != null) RealTimeUnitResultCapture.Publish(__instance.Owner?.Unit, "stat:" + __instance.Type);
        }

        [HarmonyPatch(typeof(UnitLifeController), nameof(UnitLifeController.ForceTickOnUnit))]
        [HarmonyPrefix]
        public static bool UnitLifeController_ForceTickOnUnit_Prefix() => !RealTimePresentationRuntime.IsClientActive;

        [HarmonyPatch(typeof(UnitLifeController), "SetLifeState")]
        [HarmonyPrefix]
        public static bool UnitLifeController_SetLifeState_Prefix(UnitEntityData __0) => RejectClient(__0, "life-state");

        [HarmonyPatch(typeof(UnitLifeController), "SetLifeState")]
        [HarmonyPostfix]
        public static void UnitLifeController_SetLifeState_Postfix(UnitEntityData __0) => RealTimeUnitResultCapture.Publish(__0, "life-state");

        [HarmonyPatch(typeof(UnitState), nameof(UnitState.UpdateLifeState))]
        [HarmonyPrefix]
        public static bool UnitState_UpdateLifeState_Prefix(UnitState __instance) => RejectClient(__instance.Owner?.Unit, "direct-life-state");
    }
}
