using System;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Entities.Combat;

namespace WOTRMultiplayer.Services.GameInteraction
{
    /// <summary>
    /// Resolves an ability only for a detached MechanicsContext. A same-area load can legitimately
    /// discard the original Ability fact (dead creature, consumed item, temporary ability) while a
    /// surviving buff or area context still names its blueprint. Such a context needs blueprint and
    /// caster metadata, but must never make the detached value executable through command replay.
    /// </summary>
    public static class RealTimeContextAbilityResolver
    {
        public static AbilityData Resolve(UnitEntityData caster, NetworkAbility source,
            IGameStateLookupService lookup)
        {
            if (caster == null || source == null || lookup == null) return null;
            var existing = lookup.FindAbility(caster, source);
            if (existing != null) return existing;
            if (string.IsNullOrWhiteSpace(source.BlueprintId)) return null;
            var blueprint = ResourcesLibrary.TryGetBlueprint<BlueprintAbility>(source.BlueprintId);
            return CreateDetached(caster, source, blueprint);
        }

        internal static AbilityData CreateDetached(UnitEntityData caster, NetworkAbility source,
            BlueprintAbility blueprint)
        {
            if (caster?.Descriptor == null || source == null || blueprint == null
                || !string.Equals(blueprint.AssetGuid.ToString(), source.BlueprintId,
                    StringComparison.OrdinalIgnoreCase))
                return null;
            return new AbilityData(blueprint, caster.Descriptor);
        }
    }
}
