using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kingmaker.Blueprints.Classes.Spells;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;

namespace WOTRMultiplayer.Services.CombatSaving
{
    // CombatState is entirely absent from UnitEntityData's native save contract.
    // Resolve references against the loaded world, never retain pre-load entities
    // or call setters which emit targeting/combat-start events.
    [Newtonsoft.Json.JsonObject(ItemRequired = Newtonsoft.Json.Required.AllowNull)]
    internal sealed class CombatSaveUnitRelations
    {
        public string Unit { get; set; }
        public Dictionary<string, long> Engaged { get; set; }
        public Dictionary<string, long> EngagedBy { get; set; }
        public string[] AttackedBy { get; set; }
        public string[] DisengageTargets { get; set; }
        public string ManualTarget { get; set; }
        public string LastTarget { get; set; }
        public float[] StartPosition { get; set; }
        public float[] ReturnPosition { get; set; }
        public float? ReturnOrientation { get; set; }
        public bool NeverReturn { get; set; }
        public long BuffImmunity { get; set; }
        public bool EnergyDrainImmunity { get; set; }
        public bool PreventOpportunity { get; set; }

        private static float[] Position(Vector3 position) => new[] { position.x, position.y, position.z };
        private static Vector3 Vector(float[] position) => new(position[0], position[1], position[2]);
        private static string[] Ids(IEnumerable<UnitEntityData> units)
            => units?.Select(unit => unit.UniqueId).ToArray();
        private static Dictionary<string, long> Engagements(Dictionary<UnitEntityData, TimeSpan> units)
            => units.OrderBy(pair => pair.Key.UniqueId, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key.UniqueId, pair => pair.Value.Ticks);

        internal static CombatSaveUnitRelations Capture(UnitEntityData unit)
        {
            var state = unit.CombatState;
            return new CombatSaveUnitRelations
            {
                Unit = unit.UniqueId, Engaged = Engagements(state.m_EngagedUnits), EngagedBy = Engagements(state.m_EngagedBy),
                AttackedBy = Ids(state.m_AttackedByAooUnits), DisengageTargets = Ids(state.DisengageAttackTargets),
                ManualTarget = state.m_ManualTarget?.UniqueId, LastTarget = state.m_LastTarget?.UniqueId,
                StartPosition = Position(state.StartCombatPosition),
                ReturnPosition = state.ReturnPosition.HasValue ? Position(state.ReturnPosition.Value) : null,
                ReturnOrientation = state.ReturnOrientation, NeverReturn = state.NeverReturnAfterCombat,
                BuffImmunity = (long)state.StoryModeBuffImmunity, EnergyDrainImmunity = state.StoryModeEnergyDrainImmunity,
                PreventOpportunity = state.PreventAttacksOfOpporunityNextFrame
            };
        }

        internal bool IsValid()
        {
            bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
            bool Coordinates(float[] value) => value?.Length == 3 && value.All(Finite);
            return !string.IsNullOrEmpty(Unit) && Engaged != null && EngagedBy != null && DisengageTargets != null
                && Coordinates(StartPosition) && (ReturnPosition == null || Coordinates(ReturnPosition))
                && (!ReturnOrientation.HasValue || Finite(ReturnOrientation.Value))
                && References().All(id => !string.IsNullOrEmpty(id));
        }

        internal IEnumerable<string> References() => Engaged.Keys.Concat(EngagedBy.Keys)
            .Concat(AttackedBy ?? Array.Empty<string>()).Concat(DisengageTargets)
            .Concat(new[] { ManualTarget, LastTarget }.Where(id => id != null));

        internal void Restore(IReadOnlyDictionary<string, UnitEntityData> units)
        {
            if (!IsValid() || !units.ContainsKey(Unit) || References().Any(id => !units.ContainsKey(id)))
                throw new InvalidDataException("combat-save-unit-relations-invalid");
            var state = units[Unit].CombatState;
            void Fill(Dictionary<UnitEntityData, TimeSpan> target, Dictionary<string, long> saved)
            {
                target.Clear();
                foreach (var pair in saved) target.Add(units[pair.Key], TimeSpan.FromTicks(pair.Value));
            }
            Fill(state.m_EngagedUnits, Engaged); Fill(state.m_EngagedBy, EngagedBy);
            state.m_AttackedByAooUnits = AttackedBy?.Select(id => units[id]).ToList();
            state.DisengageAttackTargets.Clear();
            state.DisengageAttackTargets.AddRange(DisengageTargets.Select(id => units[id]));
            state.m_ManualTarget = ManualTarget == null ? null : units[ManualTarget];
            state.m_LastTarget = LastTarget == null ? null : units[LastTarget];
            state.StartCombatPosition = Vector(StartPosition);
            state.ReturnPosition = ReturnPosition == null ? (Vector3?)null : Vector(ReturnPosition);
            state.ReturnOrientation = ReturnOrientation; state.NeverReturnAfterCombat = NeverReturn;
            state.StoryModeBuffImmunity = (SpellDescriptor)BuffImmunity;
            state.StoryModeEnergyDrainImmunity = EnergyDrainImmunity;
            state.PreventAttacksOfOpporunityNextFrame = PreventOpportunity;
        }
    }
}
