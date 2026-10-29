using System;
using System.IO;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TurnBased.Controllers;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Services.CombatSaving
{
    internal static class TurnMovementState
    {
        internal static bool IsCurrent(NotifyTurnMovementState frame, string session, int sessionSeed,
            int loadSeed, int turnSeed, string unit, long movement, long applied)
            => frame != null && frame.Session == session && frame.SessionSeed == sessionSeed
                && frame.LoadSeed == loadSeed && frame.TurnSeed == turnSeed && frame.Unit == unit
                && frame.Movement > 0 && frame.Movement == movement && frame.Sequence > applied;

        private static JObject UnitState(UnitEntityData unit) => unit == null ? null : new JObject
        {
            ["id"] = unit.UniqueId, ["x"] = unit.Position.x, ["y"] = unit.Position.y,
            ["z"] = unit.Position.z, ["orientation"] = unit.Orientation,
            ["actions"] = JObject.FromObject(CombatSaveUnitActions.Capture(unit))
        };

        internal static string Capture(TurnController turn) => new JObject
        {
            ["turn"] = JObject.Parse(OwlcatJsonConvert.SerializeObject(turn)),
            ["movementLimit"] = turn.CurrentMovementLimit.ToString(),
            ["rider"] = UnitState(turn.Rider), ["mount"] = UnitState(turn.Mount)
        }.ToString(Formatting.None);

        private static void ValidateUnit(JToken value, UnitEntityData unit)
        {
            if (unit == null && (value == null || value.Type == JTokenType.Null)) return;
            if (unit == null || (string)value?["id"] != unit.UniqueId)
                throw new InvalidDataException("movement-state-unit-mismatch");
            foreach (var key in new[] { "x", "y", "z", "orientation" })
            {
                var number = (float?)value[key];
                if (!number.HasValue || float.IsNaN(number.Value) || float.IsInfinity(number.Value))
                    throw new InvalidDataException("movement-state-position-invalid");
            }
            var actions = value["actions"]?.ToObject<CombatSaveUnitActions>();
            if (actions?.IsValid() != true || actions.Unit != unit.UniqueId)
                throw new InvalidDataException("movement-state-actions-invalid");
        }

        private static void ApplyUnit(JToken value, UnitEntityData unit)
        {
            if (unit == null) return;
            unit.Position = new UnityEngine.Vector3((float)value["x"], (float)value["y"], (float)value["z"]);
            unit.Orientation = (float)value["orientation"];
            value["actions"].ToObject<CombatSaveUnitActions>().Restore(unit);
        }

        internal static void Apply(TurnController turn, string json)
        {
            var payload = JObject.Parse(json);
            ValidateUnit(payload["rider"], turn.Rider);
            ValidateUnit(payload["mount"], turn.Mount);
            if (!Enum.TryParse((string)payload["movementLimit"], out TurnController.MovementLimit limit)
                || !Enum.IsDefined(typeof(TurnController.MovementLimit), limit))
                throw new InvalidDataException("movement-state-limit-invalid");
            var saved = OwlcatJsonConvert.DeserializeObject<TurnController>(payload["turn"].ToString());
            if (saved?.Rider?.UniqueId != turn.Rider.UniqueId || !TurnBasedSaveNativeState.IsStableStatus(saved.Status))
                throw new InvalidDataException("movement-state-turn-mismatch");
            // Reuse the existing subscribed controller and its command/cooldown
            // bindings. No Start, Prepare, event replay, or new action is executed.
            TurnBasedSaveNativeRestore.CopyPersistedMembers(saved, turn, "m_Rider");
            turn.CurrentMovementLimit = limit;
            ApplyUnit(payload["rider"], turn.Rider);
            ApplyUnit(payload["mount"], turn.Mount);
        }
    }
}
