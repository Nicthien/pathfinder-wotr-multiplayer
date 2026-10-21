using HarmonyLib;
using Kingmaker.Designers.Mechanics.Facts;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    // Turning a scene off removes stat modifiers one at a time. The native
    // dependency refresh can transiently reactivate AddFeatureOnSkill while
    // that teardown is incomplete, creating replacement facts and abilities
    // that did not exist at the save barrier. A native save suspension must
    // not treat that intermediate stat graph as a real gameplay transition.
    [HarmonyPatch(typeof(AddFeatureOnSkill), "OnActivate")]
    internal static class CombatSaveDerivedFactsPatch
    {
        [HarmonyPrefix]
        internal static bool Prefix() => !CombatSaveCaptureScope.Active;
    }
}
