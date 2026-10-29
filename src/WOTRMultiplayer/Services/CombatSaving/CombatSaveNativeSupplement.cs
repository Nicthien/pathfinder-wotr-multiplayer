using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TurnBased.Controllers;

namespace WOTRMultiplayer.Services.CombatSaving
{
    // The native opt-in serializer omits these live controller fields.
    // Keep them explicitly alongside its payload; never run Initialize/Prepare.
    [Newtonsoft.Json.JsonObject(ItemRequired = Newtonsoft.Json.Required.AllowNull)]
    internal sealed class CombatSaveNativeSupplement
    {
        public int RoundNumber { get; set; }
        public bool Initialized { get; set; }
        public bool HadEnemyAtSomePoint { get; set; }
        public float TimeSinceStart { get; set; }
        public float TimeToNextRound { get; set; }
        public TurnController.MovementLimit MovementLimit { get; set; }
        [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.AllowNull)]
        public string Mount { get; set; }
        [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
        public bool CanGetUpOnCommand { get; set; }
        [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
        public bool ImmuneDisengage { get; set; }
        public List<CombatSaveUnitActions> UnitActions { get; set; }
        [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
        public List<CombatSaveUnitRelations> UnitRelations { get; set; }
        [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
        public List<CombatSaveAiState> UnitAi { get; set; }

        internal static CombatSaveNativeSupplement Capture(CombatController controller) => new()
        {
            RoundNumber = controller.RoundNumber, Initialized = controller.Initialized,
            HadEnemyAtSomePoint = controller.HadEnemyAtSomePoint,
            TimeSinceStart = controller.TimeSinceStart, TimeToNextRound = controller.TimeToNextRound,
            MovementLimit = controller.CurrentTurn.CurrentMovementLimit,
            Mount = controller.CurrentTurn.Mount?.UniqueId,
            CanGetUpOnCommand = controller.CurrentTurn.UnitCanGetUpOnCommand.Value,
            ImmuneDisengage = controller.CurrentTurn.ImmuneAttackOfOpportunityOnDisengage,
            UnitActions = Kingmaker.Game.Instance.State.Units.All.Where(unit => unit.CombatState != null)
                .OrderBy(unit => unit.UniqueId, System.StringComparer.Ordinal).Select(CombatSaveUnitActions.Capture).ToList(),
            UnitRelations = Kingmaker.Game.Instance.State.Units.All.Where(unit => unit.CombatState != null)
                .OrderBy(unit => unit.UniqueId, System.StringComparer.Ordinal).Select(CombatSaveUnitRelations.Capture).ToList(),
            UnitAi = Kingmaker.Game.Instance.State.Units.All.Where(unit => unit.CombatState != null)
                .OrderBy(unit => unit.UniqueId, System.StringComparer.Ordinal).Select(CombatSaveAiState.Capture).ToList()
        };

        internal void AddTo(JObject native)
        {
            native["RoundNumber"] = RoundNumber;
            native["MultiplayerControllerState"] = JObject.FromObject(this);
        }

        internal static CombatSaveNativeSupplement Read(JObject native)
        {
            var state = native["MultiplayerControllerState"]?.ToObject<CombatSaveNativeSupplement>();
            if (state == null || state.RoundNumber < 0 || !state.Initialized
                || (int?)native["RoundNumber"] != state.RoundNumber
                || float.IsNaN(state.TimeSinceStart) || float.IsInfinity(state.TimeSinceStart)
                || float.IsNaN(state.TimeToNextRound) || float.IsInfinity(state.TimeToNextRound))
                throw new InvalidDataException("combat-save-controller-supplement-invalid");
            if (!System.Enum.IsDefined(typeof(TurnController.MovementLimit), state.MovementLimit)
                || state.UnitActions == null || state.UnitActions.Count == 0
                || state.UnitActions.Any(action => action == null || !action.IsValid())
                || state.UnitActions.Select(action => action.Unit).Distinct().Count() != state.UnitActions.Count
                || state.Mount != null && !state.UnitActions.Any(action => action.Unit == state.Mount))
                throw new InvalidDataException("combat-save-unit-actions-invalid");
            if (state.UnitRelations == null || state.UnitRelations.Any(unit => unit == null || !unit.IsValid())
                || state.UnitRelations.Select(unit => unit.Unit).Distinct().Count() != state.UnitRelations.Count
                || !state.UnitRelations.Select(unit => unit.Unit).OrderBy(id => id)
                    .SequenceEqual(state.UnitActions.Select(unit => unit.Unit).OrderBy(id => id)))
                throw new InvalidDataException("combat-save-unit-relations-invalid");
            if (state.UnitAi == null || state.UnitAi.Any(unit => unit == null || !unit.IsValid())
                || state.UnitAi.Select(unit => unit.Unit).Distinct().Count() != state.UnitAi.Count
                || !state.UnitAi.Select(unit => unit.Unit).OrderBy(id => id)
                    .SequenceEqual(state.UnitActions.Select(unit => unit.Unit).OrderBy(id => id)))
                throw new InvalidDataException("combat-save-ai-state-invalid");
            var known = new HashSet<string>(state.UnitActions.Select(unit => unit.Unit));
            if (state.UnitRelations.SelectMany(unit => unit.References()).Concat(state.UnitAi.SelectMany(unit => unit.References()))
                .Any(id => !known.Contains(id)))
                throw new InvalidDataException("combat-save-reference-outside-world");
            return state;
        }

        internal void Restore(CombatController controller, TurnController turn)
        {
            controller.RoundNumber = RoundNumber; controller.Initialized = Initialized;
            controller.HadEnemyAtSomePoint = HadEnemyAtSomePoint;
            controller.TimeSinceStart = TimeSinceStart; controller.TimeToNextRound = TimeToNextRound;
            turn.CurrentMovementLimit = MovementLimit;
            turn.ImmuneAttackOfOpportunityOnDisengage = ImmuneDisengage;
        }

        internal void RestoreUnitActions(bool restoreSavedPositions = false)
        {
            if (UnitActions == null || UnitActions.Any(state => state == null || !state.IsValid())
                || UnitActions.Select(state => state.Unit).Distinct().Count() != UnitActions.Count)
                throw new InvalidDataException("combat-save-unit-actions-invalid");
            var units = Kingmaker.Game.Instance.State.Units.All.ToDictionary(unit => unit.UniqueId);
            if (UnitActions.Any(state => !units.ContainsKey(state.Unit)))
                throw new InvalidDataException("combat-save-unit-actions-missing-unit");
            if (UnitRelations.Any(state => state.References().Any(id => !units.ContainsKey(id))))
                throw new InvalidDataException("combat-save-unit-relations-missing-unit");
            if (UnitAi.Any(state => state.References().Any(id => !units.ContainsKey(id))))
                throw new InvalidDataException("combat-save-ai-missing-unit");
            foreach (var state in UnitActions) state.Restore(units[state.Unit]);
            if (restoreSavedPositions)
                foreach (var state in UnitActions) state.RestoreSavedIdlePosition(units[state.Unit]);
            foreach (var state in UnitRelations) state.Restore(units);
            foreach (var state in UnitAi) state.Restore(units);
        }

        // OnAreaLoaded runs before the native loader has finished snapping unit
        // transforms. Rebase once more from the final OnGameLoaded positions so
        // HasMotionThisTick does not mistake loader finalization for player input.
        internal void RebaseLoadedMotionBaselines()
        {
            var units = Kingmaker.Game.Instance.State.Units.All.ToDictionary(unit => unit.UniqueId);
            if (UnitActions == null || UnitActions.Any(state => state == null || !units.ContainsKey(state.Unit)))
                throw new InvalidDataException("combat-save-unit-actions-missing-unit");
            foreach (var state in UnitActions) state.RebaseLoadedMotionBaseline(units[state.Unit]);
        }
    }
}
