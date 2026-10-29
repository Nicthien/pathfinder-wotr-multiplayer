using System;
using System.Collections.Generic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.Utility;
using UnityEngine;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services.GameInteraction
{
    public static class RealTimeBuffContextReplication
    {
        private static List<MechanicsContext> Chain(MechanicsContext root)
        {
            var chain = new List<MechanicsContext>();
            while (root != null)
            {
                if (chain.Contains(root) || chain.Count == RealTimeBuffContextChain.MaximumDepth)
                    throw new InvalidOperationException("invalid-local-buff-context-topology");
                chain.Add(root); root = root.ParentContext;
            }
            return chain;
        }

        public static string Validate(MechanicsContext root, IReadOnlyList<NetworkBuffContextState> expected)
        {
            var error = RealTimeBuffContextChain.Validate(expected);
            if (error != null || expected == null) return error;
            // Native-reference projections intentionally omit the detailed chain while retaining
            // legacy caster/source fields. An empty repeated field is therefore unspecified here,
            // not proof that the native MechanicsContext must be null.
            if (expected.Count == 0) return null;
            var local = Chain(root);
            if (local.Count != expected.Count) return "buff-context-parent-mismatch";
            for (var i = 0; i < local.Count; i++)
            {
                var node = local[i]; var state = expected[i];
                if (!Same(node.AssociatedBlueprint?.AssetGuid.ToString(), state.BlueprintId)
                    || !Same(node.m_CasterRef.Id, state.CasterId) || !Same(node.m_OwnerRef.Id, state.OwnerId)
                    || !Same(node.m_SourceItemRef.Id, state.SourceItemId)) return "buff-context-reference-mismatch";
                var localAbilityBlueprint = node is not AbilityExecutionContext ability ? null
                    : (ability.Ability?.Blueprint ?? ability.SourceAbility)?.AssetGuid.ToString();
                if (node is AbilityExecutionContext != state.IsAbilityContext
                    || node is AbilityExecutionContext && !Same(localAbilityBlueprint, state.SourceAbility?.BlueprintId))
                    return "buff-context-ability-mismatch";
                if (node.m_Ranks.Length != state.Ranks.Count || node.m_SharedValues.Length != state.SharedValues.Count
                    || node.m_Properties.Length != state.Properties.Count) return "buff-context-array-shape-mismatch";
                if ((node.m_MainTarget == null) != (state.MainTarget == null)
                    || node.m_MainTarget != null && !Same(node.m_MainTarget.Unit?.UniqueId, state.MainTarget.UnitId))
                    return "buff-context-target-reference-mismatch";
                if (node.m_MainTarget != null && state.MainTarget.UnitId == null)
                {
                    var point = node.m_MainTarget.Point;
                    if (point.x != state.MainTarget.Point.X || point.y != state.MainTarget.Point.Y || point.z != state.MainTarget.Point.Z
                        || node.m_MainTarget.Orientation != state.MainTarget.Orientation)
                        return "buff-context-point-target-mismatch";
                }
            }
            return null;
        }

        public static void Apply(MechanicsContext root, IReadOnlyList<NetworkBuffContextState> expected,
            Func<NetworkAbilityParams, AbilityParams> convertParameters)
        {
            var error = Validate(root, expected);
            if (error != null) throw new InvalidOperationException(error);
            if (expected == null || expected.Count == 0) return;
            var local = Chain(root);
            var parameters = new List<AbilityParams>();
            // Convert all parameters before touching any parent context.
            foreach (var state in expected)
                parameters.Add(convertParameters(state.Params) ?? throw new InvalidOperationException("invalid-buff-context-parameters"));
            for (var i = 0; i < local.Count; i++)
            {
                var node = local[i]; var state = expected[i];
                if (node is AbilityExecutionContext abilityContext)
                    RealTimeBuffContextCapture.RememberAbility(abilityContext, state.SourceAbility);
                state.Ranks.CopyTo(node.m_Ranks); state.SharedValues.CopyTo(node.m_SharedValues); state.Properties.CopyTo(node.m_Properties);
                node.m_Params = parameters[i];
                node.SpellDescriptor = (Kingmaker.Blueprints.Classes.Spells.SpellDescriptor)state.SpellDescriptor;
                node.SpellSchool = (Kingmaker.Blueprints.Classes.Spells.SpellSchool)state.SpellSchool;
                node.SpellLevel = state.SpellLevel; node.DisableFx = state.DisableFx;
                node.Direction = new Vector3(state.Direction.X, state.Direction.Y, state.Direction.Z);
            }
        }

        public static void ApplyConfirmedTargets(MechanicsContext root,
            IReadOnlyList<NetworkBuffContextState> expected,
            Func<string, Kingmaker.EntitySystem.Entities.UnitEntityData> resolveUnit)
        {
            var error = RealTimeBuffContextChain.Validate(expected);
            if (error != null || expected == null) throw new InvalidOperationException(error ?? "missing-buff-context-chain");
            if (expected.Count == 0) return;
            var local = Chain(root);
            if (local.Count != expected.Count) throw new InvalidOperationException("buff-context-parent-mismatch");
            for (var i = 0; i < local.Count; i++)
            {
                var target = expected[i].MainTarget;
                if (target == null)
                {
                    local[i].m_MainTarget = null;
                    continue;
                }
                Kingmaker.EntitySystem.Entities.UnitEntityData unit = null;
                if (!string.IsNullOrWhiteSpace(target.UnitId))
                {
                    unit = resolveUnit(target.UnitId);
                    if (unit == null || !Same(unit.UniqueId, target.UnitId))
                        throw new InvalidOperationException("unresolved-buff-context-target-unit");
                }
                local[i].m_MainTarget = new TargetWrapper(
                    new Vector3(target.Point.X, target.Point.Y, target.Point.Z), target.Orientation, unit);
            }
        }

        private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
