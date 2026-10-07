using System;
using System.Collections.Generic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Mechanics;
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
            var local = Chain(root);
            if (local.Count != expected.Count) return "buff-context-parent-mismatch";
            for (var i = 0; i < local.Count; i++)
            {
                var node = local[i]; var state = expected[i];
                if (!Same(node.AssociatedBlueprint?.AssetGuid.ToString(), state.BlueprintId)
                    || !Same(node.m_CasterRef.Id, state.CasterId) || !Same(node.m_OwnerRef.Id, state.OwnerId)
                    || !Same(node.m_SourceItemRef.Id, state.SourceItemId)) return "buff-context-reference-mismatch";
                if (node is AbilityExecutionContext != state.IsAbilityContext
                    || node is AbilityExecutionContext ability && !Same(ability.Ability?.Blueprint?.AssetGuid.ToString(), state.SourceAbility?.BlueprintId))
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
            if (expected == null) return;
            var local = Chain(root);
            var parameters = new List<AbilityParams>();
            // Convert all parameters before touching any parent context.
            foreach (var state in expected)
                parameters.Add(convertParameters(state.Params) ?? throw new InvalidOperationException("invalid-buff-context-parameters"));
            for (var i = 0; i < local.Count; i++)
            {
                var node = local[i]; var state = expected[i];
                state.Ranks.CopyTo(node.m_Ranks); state.SharedValues.CopyTo(node.m_SharedValues); state.Properties.CopyTo(node.m_Properties);
                node.m_Params = parameters[i];
                node.SpellDescriptor = (Kingmaker.Blueprints.Classes.Spells.SpellDescriptor)state.SpellDescriptor;
                node.SpellSchool = (Kingmaker.Blueprints.Classes.Spells.SpellSchool)state.SpellSchool;
                node.SpellLevel = state.SpellLevel; node.DisableFx = state.DisableFx;
                node.Direction = new Vector3(state.Direction.X, state.Direction.Y, state.Direction.Z);
            }
        }

        private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
