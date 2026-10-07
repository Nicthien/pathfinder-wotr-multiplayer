using HarmonyLib;
using Kingmaker.Controllers.Projectiles;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public static class RealTimeProjectileSafetyPatches
    {
        [HarmonyPatch(typeof(Projectile), nameof(Projectile.OnHit))]
        [HarmonyPrefix]
        public static bool Projectile_OnHit_Prefix(Projectile __instance)
        {
            if (!RealTimePresentationRuntime.IsClientActive) return true;
            // OnHit dispatches projectile handlers, Callback and OnHitTrigger.
            // None of those may run merely to display the host's impact.
            Main.Multiplayer.ReportRealTimeAIActionFault(__instance.Launcher?.Unit?.UniqueId, null,
                "autonomous-client-projectile-impact");
            return false;
        }
    }
}
