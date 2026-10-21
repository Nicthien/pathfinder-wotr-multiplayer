using HarmonyLib;
using Kingmaker;
using Kingmaker.Armies.TacticalCombat;
using Kingmaker.Controllers.Combat;
using Kingmaker.UnitLogic.Commands.Base;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch(typeof(UnitCombatState), nameof(UnitCombatState.HasCooldownForCommand), new[] { typeof(UnitCommand) })]
    internal sealed class ConfirmedAbilityReplayPatches
    {
        [HarmonyPrefix]
        private static bool Prefix(UnitCommand __0, ref bool __result)
        {
            if (!Main.Multiplayer.IsActive || Main.Multiplayer.IsRealTimeClientCombatActive
                || TacticalCombatHelper.IsActive || Game.Instance.TurnBasedCombatController?.CurrentTurn == null
                || __0 == null || __0.IsStarted || !ConfirmedAbilityReplay.Contains(__0)) return true;

            // Keep availability, incapacitation, range and OnStart spending intact.
            // Only the pre-start action-budget veto is redundant for this replay.
            __result = false;
            if (ConfirmedAbilityReplay.ReportOnce(__0))
                Main.GetLogger<ConfirmedAbilityReplayPatches>().LogInformation(
                    "[AbilityReplay] Confirmed cast allowed through local pre-start budget. UnitId={UnitId}, Command={Command}",
                    __0.Executor?.UniqueId, __0.GetType().Name);
            return false;
        }
    }
}
