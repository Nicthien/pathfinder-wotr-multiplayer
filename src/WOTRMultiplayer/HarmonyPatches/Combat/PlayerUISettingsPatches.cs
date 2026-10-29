using HarmonyLib;
using Kingmaker.UI;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public class PlayerUISettingsPatches
    {
        [HarmonyPatch(typeof(PlayerUISettings), nameof(PlayerUISettings.DoSpeedUp))]
        [HarmonyPrefix]
        public static bool PlayerUISettings_DoSpeedUp_Prefix()
        {
            // Multiplayer enforces the same turn animation speed on both peers.
            // Do not swallow the native Space speed-up input on top of that.
            return true;
        }
    }
}
