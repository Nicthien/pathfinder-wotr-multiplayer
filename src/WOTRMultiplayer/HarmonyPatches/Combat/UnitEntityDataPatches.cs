using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using System;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public class UnitEntityDataPatches
    {
        [HarmonyPatch(typeof(UnitEntityData), nameof(UnitEntityData.JoinCombat))]
        [HarmonyPrefix]
        public static bool UnitEntityData_JoinCombat_Prefix(UnitEntityData __instance)
        {
            return ShouldAllowJoin(Main.Multiplayer.IsActive,
                RealTimePresentationRuntime.IsClientActive,
                () => Main.Multiplayer.CanUnitJoinCombat(__instance.UniqueId, __instance.GroupId));
        }

        public static bool ShouldAllowJoin(bool multiplayerActive, bool realTimePresentationClient,
            Func<bool> multiplayerGate)
        {
            if (!multiplayerActive) return true;
            // In host-authoritative RTwP, proximity/group controllers on the client
            // can notice a newly encountered unit and attempt a full native combat
            // join. The host snapshot already supplies its presentation, so reject
            // the local simulation before it rolls initiative or mutates membership.
            if (realTimePresentationClient) return false;
            return multiplayerGate();
        }
    }
}
