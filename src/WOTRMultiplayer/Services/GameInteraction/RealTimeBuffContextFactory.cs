using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using Kingmaker.Items;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.Utility;
using UnityEngine;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Detached presentation contexts only: never call Recalculate, Setup or
    // an ordinary ability-context constructor while restoring host results.
    public static class RealTimeBuffContextFactory
    {
        public static MechanicsContext Create(IReadOnlyList<NetworkBuffContextState> states,
            Func<string, BlueprintScriptableObject> blueprint,
            Func<string, UnitEntityData> unit,
            Func<string, ItemEntity> item,
            Func<NetworkBuffContextState, AbilityData> ability,
            Func<NetworkAbilityParams, AbilityParams> parameters)
        {
            var error = RealTimeBuffContextChain.Validate(states);
            if (error != null || states == null)
                throw new InvalidOperationException(error ?? "missing-buff-context-chain");
            var nodes = new List<MechanicsContext>();
            foreach (var state in states)
            {
                var source = blueprint(state.BlueprintId);
                if (ReferenceEquals(source, null) || !Same(source.AssetGuid.ToString(), state.BlueprintId))
                    throw new InvalidOperationException("unresolved-buff-context-blueprint");
                MechanicsContext node;
                if (state.IsAbilityContext)
                {
                    var resolved = ability(state);
                    if (resolved == null || !Same(resolved.Blueprint?.AssetGuid.ToString(), state.SourceAbility.BlueprintId))
                        throw new InvalidOperationException("unresolved-buff-context-ability");
                    node = new AbilityExecutionContext(default(JsonConstructorMark));
                    AccessTools.Field(typeof(AbilityExecutionContext), "Ability").SetValue(node, resolved);
                }
                else node = new MechanicsContext(default(JsonConstructorMark));
                AccessTools.Field(typeof(MechanicsContext), "AssociatedBlueprint").SetValue(node, source);
                if (state.CasterId != null) node.m_CasterRef = ResolveUnit(state.CasterId, unit);
                if (state.OwnerId != null)
                {
                    var ownerReference = node.m_CasterRef;
                    ownerReference = ResolveUnit(state.OwnerId, unit);
                    AccessTools.Field(typeof(MechanicsContext), "m_OwnerRef").SetValue(node, ownerReference);
                }
                if (state.SourceItemId != null)
                {
                    var resolved = item(state.SourceItemId);
                    if (resolved == null || !Same(resolved.UniqueId, state.SourceItemId))
                        throw new InvalidOperationException("unresolved-buff-context-item");
                    node.m_SourceItemRef = resolved;
                }
                if (state.MainTarget != null)
                {
                    var target = state.MainTarget;
                    node.m_MainTarget = new TargetWrapper(new Vector3(target.Point.X, target.Point.Y, target.Point.Z),
                        target.Orientation, target.UnitId == null ? null : ResolveUnit(target.UnitId, unit));
                }
                nodes.Add(node);
            }
            for (var i = 0; i + 1 < nodes.Count; i++) nodes[i].ParentContext = nodes[i + 1];
            RealTimeBuffContextReplication.Apply(nodes[0], states, parameters);
            return nodes[0];
        }

        private static UnitEntityData ResolveUnit(string id, Func<string, UnitEntityData> resolve)
        {
            var value = resolve(id);
            if (value == null || !Same(value.UniqueId, id))
                throw new InvalidOperationException("unresolved-buff-context-unit");
            return value;
        }
        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
