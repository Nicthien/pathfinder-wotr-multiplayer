using System;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using Newtonsoft.Json;
using TurnBased.Controllers;

namespace WOTRMultiplayer.Services.CombatSaving
{
    internal static class TurnBasedSaveNativeState
    {
        internal static void RequireReady() => RequireReady(restoring: false, expectedUnit: null);

        private static void RequireReady(bool restoring, string expectedUnit)
        {
            var reason = GetReadinessFailure(restoring, expectedUnit);
            if (reason != null) throw new InvalidOperationException(reason);
        }

        internal static bool IsRestoreCaptureReady(string expectedUnit)
            => GetReadinessFailure(restoring: true, expectedUnit) == null;

        internal static string DescribeRestoreReadiness(string expectedUnit)
            => GetReadinessFailure(restoring: true, expectedUnit) ?? "ready";

        private static string GetReadinessFailure(bool restoring, string expectedUnit)
        {
            var game = Game.Instance;
            var controller = game?.TurnBasedCombatController;
            var turn = controller?.CurrentTurn;
            var rider = turn?.Rider;
            var inCombat = game != null && CombatController.IsInTurnBasedCombat();
            var riderPending = HasPendingAction(rider);
            var mountPending = HasPendingAction(turn?.Mount);
            if (game != null && CombatSavePauseLease.IsWorldPaused(game)
                && IsCombatLifecycleReady(inCombat, restoring) && rider != null
                && (expectedUnit == null || rider.UniqueId == expectedUnit) && rider.State.IsFinallyDead != true
                && !turn.HasRunningCommands && !riderPending && !mountPending && IsStableStatus(turn.Status))
                return null;
            return (restoring ? "combat-save-restored-turn-not-ready" : "combat-save-native-turn-not-ready")
                + ":status=" + turn?.Status + ";running=" + turn?.HasRunningCommands
                + ";inCombat=" + inCombat + ";rider=" + rider?.UniqueId + ";expected=" + expectedUnit
                + ";dead=" + rider?.State.IsFinallyDead + ";riderPending=" + riderPending
                + ";mountPending=" + mountPending + ";view=" + (rider?.View != null)
                + ";getUp=" + rider?.View?.IsGetUp + ";motion=" + rider?.HasMotionThisTick
                + ";raw=" + rider?.Commands.Raw.Count(command => command != null && !command.IsFinished)
                + ";queued=" + rider?.Commands.Queue.Count(command => command != null && !command.IsFinished);
        }

        // OnAreaLoaded restores the controller before the native OnGameLoaded /
        // combat-state notification. A normal save still requires the fully
        // active combat lifecycle; validation of the just-rebound controller
        // must not depend on that later notification.
        internal static bool IsCombatLifecycleReady(bool inCombat, bool restoring)
            => restoring || inCombat;

        internal static bool IsRestoreVerificationLifecycleReady(bool gameLoaded, bool inCombat)
            => gameLoaded && inCombat;

        // Scrolling calls Prepare on its next tick, which clears cooldowns.
        // Acting persists after the first command, including while the player
        // chooses the next action. Pending commands are checked separately.
        // Delayed/ending turns must not be resurrected as an active turn.
        internal static bool IsStableStatus(TurnController.TurnStatus status)
            => status == TurnController.TurnStatus.Preparing || status == TurnController.TurnStatus.Acting;

        internal static bool HasPendingAction(UnitEntityData unit) => unit != null
            && (unit.Commands.Raw.Any(command => command != null && !command.IsFinished)
                || unit.Commands.Queue.Any(command => command != null && !command.IsFinished)
                || unit.View == null || unit.View.IsGetUp || unit.HasMotionThisTick);

        // This is the SAME native serializer used by SaveManager's tbm task.
        // Retain the whole controller, not a reconstructed new TurnController.
        internal static string Capture()
        {
            RequireReady();
            return SerializeCurrent();
        }

        internal static string CaptureRestored(string expectedUnit)
        {
            RequireReady(restoring: true, expectedUnit: expectedUnit);
            return SerializeCurrent();
        }

        private static string SerializeCurrent()
        {
            var json = OwlcatJsonConvert.SerializeObject(Game.Instance.TurnBasedCombatController, Formatting.None);
            GameInteraction.RealTimeNativeCheckpointLoadSource.ValidateJson(json, true);
            var native = Newtonsoft.Json.Linq.JObject.Parse(json);
            CombatSaveNativeSupplement.Capture(Game.Instance.TurnBasedCombatController).AddTo(native);
            return native.ToString(Formatting.None);
        }
    }
}
