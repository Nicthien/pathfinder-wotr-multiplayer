using HarmonyLib;
using Kingmaker.Controllers.Combat;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch(typeof(UnitCombatLeaveController), nameof(UnitCombatLeaveController.Tick))]
    public static class CombatStartupPatches
    {
        // Native inactivity is not a combat-end decision while peers are still
        // collecting the roster or waiting for the first turn acknowledgement.
        [HarmonyPrefix]
        public static bool BeforeLeaveTick() => Main.Multiplayer?.HoldsCombatMembership != true;
    }
}
