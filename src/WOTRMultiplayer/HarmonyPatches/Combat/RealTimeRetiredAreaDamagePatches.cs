using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public static class RealTimeRetiredAreaDamagePatches
    {
        private static readonly Type Adapter = typeof(AreaEffectEntityData)
            .GetNestedType("DamageTriggerAdapter", BindingFlags.NonPublic)
            ?? throw new MissingMemberException("AreaEffectEntityData.DamageTriggerAdapter");
        private static readonly FieldInfo Area = AccessTools.Field(Adapter, "m_Area")
            ?? throw new MissingFieldException(Adapter.FullName, "m_Area");

        [HarmonyPrepare]
        public static bool Prepare() => RealTimePresentationRuntime.Enabled;

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> Targets() => Adapter.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == "OnEventDidTrigger");

        [HarmonyPrefix]
        public static bool BeforeDamage(object __instance)
        {
            var area = (AreaEffectEntityData)Area.GetValue(__instance);
            // Quarantined adapters must stay inert even after disconnect, when
            // an incomplete rollback restored only some native subscriptions.
            if (RealTimeAreaReplicaFactory.IsReplica(area)) return false;
            if (!RealTimePresentationRuntime.IsClientActive) return true;
            Main.Multiplayer.ReportRealTimeAIActionFault(null, null,
                "autonomous-client-area-damage-adapter:" + area?.UniqueId);
            return false;
        }
    }
}
