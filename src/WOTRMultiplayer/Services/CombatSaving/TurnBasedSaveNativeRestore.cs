using System;
using System.IO;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using Newtonsoft.Json.Serialization;
using TurnBased.Controllers;
using UniRx;

namespace WOTRMultiplayer.Services.CombatSaving
{
    internal static class TurnBasedSaveNativeRestore
    {
        // Native ThreadedGameLoader ignores tbm.json. Rebind the saved turn to the
        // loaded entities without Start/Prepare (both reset mechanical state).
        internal static void Restore(string json, string expectedUnit, int expectedRound)
        {
            var game = Game.Instance;
            if (game?.State == null || !CombatSavePauseLease.IsWorldPaused(game)) throw new InvalidOperationException("combat-save-restore-requires-pause");
            GameInteraction.RealTimeNativeCheckpointLoadSource.ValidateJson(json, true);
            var supplement = CombatSaveNativeSupplement.Read(Newtonsoft.Json.Linq.JObject.Parse(json));
            supplement.RestoreUnitActions();
            var saved = OwlcatJsonConvert.DeserializeObject<CombatController>(json)
                ?? throw new InvalidDataException("combat-save-controller-missing");
            var turn = saved.CurrentTurn;
            var rider = turn?.Rider;
            if (rider == null || rider.UniqueId != expectedUnit || supplement.RoundNumber != expectedRound
                || rider.State.IsFinallyDead || !TurnBasedSaveNativeState.IsStableStatus(turn.Status) || turn.HasRunningCommands
                || TurnBasedSaveNativeState.HasPendingAction(rider) || TurnBasedSaveNativeState.HasPendingAction(turn.Mount))
                throw new InvalidDataException("combat-save-turn-restore-mismatch:rider=" + rider?.UniqueId
                    + ";expected=" + expectedUnit + ";round=" + supplement.RoundNumber
                    + ";status=" + turn?.Status + ";dead=" + rider?.State.IsFinallyDead
                    + ";running=" + turn?.HasRunningCommands
                    + ";riderPending=" + (rider != null && TurnBasedSaveNativeState.HasPendingAction(rider))
                    + ";mountPending=" + (turn?.Mount != null && TurnBasedSaveNativeState.HasPendingAction(turn.Mount))
                    + ";view=" + (rider?.View != null) + ";motion=" + rider?.HasMotionThisTick);
            var controller = game.TurnBasedCombatController;
            TurnController rebound = null;
            try
            {
                // Dispose old subscriptions before constructing the new subscriber.
                var loadedTurn = controller.CurrentTurn;
                controller.CurrentTurn = null;
                loadedTurn?.Dispose();
                // The unit constructor binds commands/cooldowns/mount and subscribes
                // events; it does not call Start, Prepare or SpendAction (IL audited).
                rebound = new TurnController(rider);
                CopyPersistedMembers(turn, rebound, null);
                if (rebound.Mount?.UniqueId != supplement.Mount
                    || TurnBasedSaveNativeState.HasPendingAction(rebound.Mount))
                    throw new InvalidDataException("combat-save-restored-mount-mismatch");
                // Start normally installs this subscription but also resets actions.
                // Suppress the ReactiveProperty's initial emission: restoring a turn
                // must not complete a stand-up action as a side effect of subscribing.
                rebound.UnitCanGetUpOnCommand.Value = supplement.CanGetUpOnCommand;
                rebound.m_UnitCanGetUpOnCommandDispose = rebound.UnitCanGetUpOnCommand.Skip(1)
                    .Subscribe(rebound.UnitCanGetUpCommandChanged);
                CopyPersistedMembers(saved, controller, "m_CurrentTurn");
                ClearRestoredNavmeshUpdates(controller);
                controller.CurrentTurn = rebound;
                supplement.Restore(controller, rebound);
                var expectedProjection = TurnBasedSaveDigest.Project(Newtonsoft.Json.Linq.JObject.Parse(json));
                var actualProjection = TurnBasedSaveDigest.Project(Newtonsoft.Json.Linq.JObject.Parse(
                    TurnBasedSaveNativeState.CaptureRestored(expectedUnit)));
                if (actualProjection != expectedProjection)
                    throw new InvalidDataException("combat-save-rebound-turn-differs:"
                        + TurnBasedSaveDigest.DescribeFirstDifference(expectedProjection, actualProjection));
            }
            catch
            {
                if (!ReferenceEquals(controller.CurrentTurn, rebound)) rebound?.Dispose();
                // Do not resume a partly restored world or silently start a fresh turn.
                controller.CurrentTurn = null;
                game.IsPaused = true;
                throw;
            }
        }

        // GraphUpdateObject is present in the native JSON contract, but its
        // pathfinding payload is not serialized.  Owlcat therefore recreates an
        // apparently non-null shell which crashes RecastGraph.UpdateAreaInit on
        // the first turn change after loading.  The navigation graph itself is
        // rebuilt by the area loader; let StartTurn create fresh update objects.
        internal static int ClearRestoredNavmeshUpdates(CombatController controller)
        {
            if (controller?.m_Units == null) return 0;
            var cleared = 0;
            foreach (var unit in controller.m_Units)
            {
                if (unit?.LastNavmeshUpdateObject == null) continue;
                unit.LastNavmeshUpdateObject = null;
                cleared++;
            }
            return cleared;
        }

        internal static void CopyPersistedMembers(object source, object destination, string excluded)
        {
            if (source == null || destination == null || source.GetType() != destination.GetType())
                throw new ArgumentException("combat-save-member-types-differ");
            var contract = new OptInContractResolver().ResolveContract(source.GetType()) as JsonObjectContract
                ?? throw new InvalidOperationException("combat-save-contract-unsupported");
            foreach (var property in contract.Properties.Where(p => !p.Ignored && p.Readable && p.Writable
                && p.UnderlyingName != excluded && p.PropertyName != excluded))
                property.ValueProvider.SetValue(destination, property.ValueProvider.GetValue(source));
        }
    }
}
