using HarmonyLib;
using Kingmaker.Designers.Mechanics.Facts;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch(typeof(AddBuffOnCombatStart), "OnTurnOff")]
    internal static class CombatSaveCombatStartBuffPatch
    {
        // Native OnTurnOff removes AppliedBuff and only an initiative roll adds
        // it again. Suspending entities to save must not end this combat effect.
        [HarmonyPrefix]
        internal static bool Prefix() => !CombatSaveCaptureScope.Active;
    }
}
