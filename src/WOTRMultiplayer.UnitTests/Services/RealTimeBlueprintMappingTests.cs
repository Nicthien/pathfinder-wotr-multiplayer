using AutoMapper;
using NUnit.Framework;
using WOTRMultiplayer.Config.Mapping;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeBlueprintMappingTests
    {
        [Test]
        public void CombatCheckpointMapping_PreservesUnitAndAreaBlueprintIds()
        {
            var mapper = new MapperConfiguration(configuration =>
                configuration.AddProfile<NetworkMessagesProfile>()).CreateMapper();
            var source = new NetworkCombatState
            {
                Units = [new NetworkUnit { Id = "summon", BlueprintId = "unit-blueprint" }],
                AreaEffects = [new NetworkAreaEffect { Id = "cloud", BlueprintId = "area-blueprint" }]
            };

            var wire = mapper.Map<Networking.Messages.Contracts.NetworkCombatState>(source);
            var restored = mapper.Map<NetworkCombatState>(wire);

            Assert.That(wire.Units[0].BlueprintId, Is.EqualTo("unit-blueprint"));
            Assert.That(wire.AreaEffects[0].BlueprintId, Is.EqualTo("area-blueprint"));
            Assert.That(restored.Units[0].BlueprintId, Is.EqualTo("unit-blueprint"));
            Assert.That(restored.AreaEffects[0].BlueprintId, Is.EqualTo("area-blueprint"));
        }
    }
}
