using HarmonyLib;
using Kingmaker;
using Kingmaker.Controllers;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch(typeof(TimeController), nameof(TimeController.Tick))]
    public static class CombatSaveClockPatches
    {
        [HarmonyPrefix]
        public static bool BeforeTick()
        {
            var game = Game.Instance;
            if (!CombatSavePauseLease.HoldsWorld(game)) return true;
            CombatSavePauseLease.FreezeClock(game);
            return false;
        }
    }

    // A zero delta does not stop native unit/cutscene ticks from starting effects
    // or commands. Hold these only after the new world is bound, so native loading
    // and menu/network updates remain available throughout the handshake.
    [HarmonyPatch]
    public static class CombatSaveWorldControllerPatches
    {
        [HarmonyTargetMethods]
        public static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(Kingmaker.Controllers.Units.BaseUnitController), "Tick");
            yield return AccessTools.Method(typeof(CutsceneController), "Tick");
            // This controller recomputes melee reach every frame. During a
            // native load, CanAct/threat-hand state is still transitional and
            // Tick can therefore erase the engagement graph restored from the
            // combat-save supplement before its digest is verified.
            yield return AccessTools.Method(typeof(Kingmaker.Controllers.Combat.UnitCombatEngagementController), "Tick");
        }
        [HarmonyPrefix]
        public static bool BeforeTick() => !CombatSaveLoadState.HoldsWorld(Game.Instance?.State);
    }
}
