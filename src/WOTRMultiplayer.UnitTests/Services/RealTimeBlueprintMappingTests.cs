using System;
using System.Reflection;
using AutoMapper;
using Kingmaker.UnitLogic.Parts;
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
        public void TemporaryNegativeLevelMapping_UsesRemainingDuration()
        {
            var mapper = new MapperConfiguration(configuration =>
                configuration.AddProfile<GameProfile>()).CreateMapper();
            var levelType = typeof(UnitPartNegativeLevels).GetNestedType("Data",
                BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(levelType, Is.Not.Null);
            var level = Activator.CreateInstance(levelType, true);
            levelType.GetField("Count", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SetValue(level, 1);
            levelType.GetField("EndTime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SetValue(level, (TimeSpan?)TimeSpan.FromMinutes(12));

            var network = (NetworkUnitNegativeLevelsData)mapper.Map(level, levelType,
                typeof(NetworkUnitNegativeLevelsData),
                options => options.Items[GameProfile.BuffBaseTimeItem] = TimeSpan.FromMinutes(2));

            Assert.That(network.Duration, Is.EqualTo(TimeSpan.FromMinutes(10)));
        }

        [Test]
        public void CombatCheckpointMapping_PreservesUnitAndAreaBlueprintIds()
        {
            var mapper = new MapperConfiguration(configuration =>
                configuration.AddProfile<NetworkMessagesProfile>()).CreateMapper();
            var source = new NetworkCombatState
            {
                Units = [new NetworkUnit
                {
                    Id = "summon", BlueprintId = "unit-blueprint", OriginalBlueprintId = "original-unit",
                    CasterUnitId = "caster", SourceAbilityBlueprintId = "summon-spell",
                    MasterUnitId = "master", GroupId = "summoned-group", SceneName = "combat-scene"
                }],
                AreaEffects = [new NetworkAreaEffect
                {
                    Id = "cloud", BlueprintId = "area-blueprint", CasterUnitId = "caster",
                    OwnerUnitId = "owner", SourceAbilityBlueprintId = "source-ability",
                    CreationGameTimeTicks = 123456, DurationTicks = 60000000,
                    SceneName = "combat-scene", TargetUnitId = "summon",
                    TargetOrientation = 1.5f, IsAttachedToUnit = true
                }]
            };

            var wire = mapper.Map<Networking.Messages.Contracts.NetworkCombatState>(source);
            var restored = mapper.Map<NetworkCombatState>(wire);

            Assert.That(wire.Units[0].BlueprintId, Is.EqualTo("unit-blueprint"));
            Assert.That(wire.AreaEffects[0].BlueprintId, Is.EqualTo("area-blueprint"));
            Assert.That(restored.Units[0].BlueprintId, Is.EqualTo("unit-blueprint"));
            Assert.That(restored.Units[0].OriginalBlueprintId, Is.EqualTo("original-unit"));
            Assert.That(restored.Units[0].CasterUnitId, Is.EqualTo("caster"));
            Assert.That(restored.Units[0].MasterUnitId, Is.EqualTo("master"));
            Assert.That(restored.Units[0].GroupId, Is.EqualTo("summoned-group"));
            Assert.That(restored.Units[0].SceneName, Is.EqualTo("combat-scene"));
            Assert.That(restored.AreaEffects[0].BlueprintId, Is.EqualTo("area-blueprint"));
            Assert.That(wire.AreaEffects[0].CasterUnitId, Is.EqualTo("caster"));
            Assert.That(restored.AreaEffects[0].OwnerUnitId, Is.EqualTo("owner"));
            Assert.That(restored.AreaEffects[0].SourceAbilityBlueprintId, Is.EqualTo("source-ability"));
            Assert.That(restored.AreaEffects[0].CreationGameTimeTicks, Is.EqualTo(123456));
            Assert.That(restored.AreaEffects[0].DurationTicks, Is.EqualTo(60000000));
            Assert.That(restored.AreaEffects[0].SceneName, Is.EqualTo("combat-scene"));
            Assert.That(restored.AreaEffects[0].TargetUnitId, Is.EqualTo("summon"));
            Assert.That(restored.AreaEffects[0].TargetOrientation, Is.EqualTo(1.5f));
            Assert.That(restored.AreaEffects[0].IsAttachedToUnit, Is.True);
        }
    }
}
