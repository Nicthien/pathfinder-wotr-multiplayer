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
        public static System.Collections.Generic.List<NetworkBuffContextState> Capture(MechanicsContext context, IRuntimeMapper mapper)
        {
            if (context == null) return null;
            return RealTimeBuffContextChain.Capture(context, node => node.ParentContext, node => new NetworkBuffContextState
            {
                BlueprintId = node.AssociatedBlueprint?.AssetGuid.ToString(),
                CasterId = node.m_CasterRef.Id, OwnerId = node.m_OwnerRef.Id,
                IsAbilityContext = node is AbilityExecutionContext,
                SourceAbility = node is AbilityExecutionContext ability ? mapper.Map<NetworkAbility>(ability.Ability) : null,
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
