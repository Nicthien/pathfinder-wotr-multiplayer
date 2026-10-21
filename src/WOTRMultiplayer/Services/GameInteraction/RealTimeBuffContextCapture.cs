using System.Linq;
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
        public static NetworkAbility CaptureAbility(AbilityExecutionContext context, IRuntimeMapper mapper)
        {
            if (context == null) return null;
            if (!CombatSaveDigestScope.Active) return mapper.Map<NetworkAbility>(context.Ability);
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
