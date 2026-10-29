using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.Designers.Mechanics.Buffs;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    internal static class CombatSaveStatusConditionPatch
    {
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(BuffStatusCondition), "OnTurnOff")
                ?? throw new MissingMethodException(typeof(BuffStatusCondition).FullName, "OnTurnOff");
            yield return AccessTools.Method(typeof(BuffStatusCondition), "OnTurnOn")
                ?? throw new MissingMethodException(typeof(BuffStatusCondition).FullName, "OnTurnOn");
        }

        // Native checkpoint capture temporarily turns the whole scene off and
        // back on. BuffStatusCondition normally removes and recreates its
        // derived status buff during that lifecycle, changing its fact id even
        // though gameplay is frozen. Preserve the already existing status fact
        // for the duration of the capture; its own entity lifecycle is still
        // handled by the scene's facts manager.
        [HarmonyPrefix]
        internal static bool Prefix() => !CombatSaveCaptureScope.Active;
    }
}
