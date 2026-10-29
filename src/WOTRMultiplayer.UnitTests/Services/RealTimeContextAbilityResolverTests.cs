using System.Runtime.Serialization;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeContextAbilityResolverTests
    {
        private static T Bare<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));

        [Test]
        public void CreatesDetachedContextAbilityWhenNativeFactWasDiscardedByLoad()
        {
            const string id = "91da41b9793a4624797921f221db653c";
            var caster = Bare<UnitEntityData>();
            typeof(UnitEntityData).GetField("<Descriptor>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(caster, Bare<UnitDescriptor>());
            var blueprint = Bare<BlueprintAbility>();
            blueprint.AssetGuid = BlueprintGuid.Parse(id);

            var ability = RealTimeContextAbilityResolver.CreateDetached(caster,
                new NetworkAbility { BlueprintId = id }, blueprint);

            Assert.That(ability, Is.Not.Null);
            Assert.That(ability.Blueprint, Is.SameAs(blueprint));
        }

        [Test]
        public void RefusesDetachedContextAbilityWithWrongBlueprint()
        {
            var caster = Bare<UnitEntityData>();
            typeof(UnitEntityData).GetField("<Descriptor>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(caster, Bare<UnitDescriptor>());
            var blueprint = Bare<BlueprintAbility>();
            blueprint.AssetGuid = BlueprintGuid.Parse("11111111111111111111111111111111");

            Assert.That(RealTimeContextAbilityResolver.CreateDetached(caster,
                new NetworkAbility { BlueprintId = "22222222222222222222222222222222" }, blueprint), Is.Null);
        }
    }
}
