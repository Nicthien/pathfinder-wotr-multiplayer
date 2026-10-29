using System.Linq;
using System.Runtime.CompilerServices;
using AutoMapper;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Mechanics;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services.GameInteraction
{
    public static class RealTimeBuffContextCapture
    {
        private sealed class ConfirmedAbility
        {
            public NetworkAbility Value { get; set; }
        }

        private static readonly ConditionalWeakTable<AbilityExecutionContext, ConfirmedAbility>
            ConfirmedAbilities = new();

        internal static void RememberAbility(AbilityExecutionContext context, NetworkAbility ability)
        {
            if (context == null || ability == null) return;
            ConfirmedAbilities.GetOrCreateValue(context).Value = RealTimeBuffReplicaStore.CloneAbility(ability);
        }

        public static NetworkAbility CaptureAbility(AbilityExecutionContext context, IRuntimeMapper mapper)
        {
            if (context == null) return null;
            if (!CombatSaveDigestScope.Active)
            {
                if (ConfirmedAbilities.TryGetValue(context, out var confirmed) && confirmed.Value != null)
                    return RealTimeBuffReplicaStore.CloneAbility(confirmed.Value);
                var runtimeAbility = mapper.Map<NetworkAbility>(context.Ability);
                if (!string.IsNullOrWhiteSpace(runtimeAbility?.BlueprintId)) return runtimeAbility;
            }

            // Native loads may legitimately restore an AbilityExecutionContext
            // before its transient AbilityData reference. SourceAbility remains
            // the authoritative blueprint in that window and is sufficient for
            // rebuilding the detached presentation context. Treat it the same
            // way as the save-digest projection instead of rejecting the whole
            // combat snapshot and entering an endless repair loop.
            var blueprint = context.Ability?.Blueprint ?? context.SourceAbility;
            return blueprint == null ? null : new NetworkAbility { BlueprintId = blueprint.AssetGuid.ToString() };
        }

        public static System.Collections.Generic.List<NetworkBuffContextState> Capture(MechanicsContext context, IRuntimeMapper mapper)
        {
            if (context == null) return null;
            return RealTimeBuffContextChain.Capture(context, node => node.ParentContext, node => new NetworkBuffContextState
            {
                BlueprintId = node.AssociatedBlueprint?.AssetGuid.ToString(),
                CasterId = node.m_CasterRef.Id, OwnerId = node.m_OwnerRef.Id,
                IsAbilityContext = node is AbilityExecutionContext,
                SourceAbility = node is AbilityExecutionContext ability ? CaptureAbility(ability, mapper) : null,
                // Params is a read-only fallback through ParentContext/Empty.
                // No Recalculate, rule, context cloning or save lifecycle.
                Params = mapper.Map<NetworkAbilityParams>(node.Params),
                Ranks = node.m_Ranks.ToList(), SharedValues = node.m_SharedValues.ToList(), Properties = node.m_Properties.ToList(),
                SpellDescriptor = (long)node.SpellDescriptor, SpellSchool = (int)node.SpellSchool, SpellLevel = node.SpellLevel,
                DisableFx = node.DisableFx,
                Direction = new NetworkVector3(node.Direction.x, node.Direction.y, node.Direction.z),
                SourceItemId = node.m_SourceItemRef.Id,
                MainTarget = node.m_MainTarget == null ? null : mapper.Map<NetworkTargetWrapper>(node.m_MainTarget)
            });
        }
    }
}
