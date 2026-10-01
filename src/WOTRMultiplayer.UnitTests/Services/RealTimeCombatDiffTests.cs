using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeCombatDiffTests
    {
        [Test]
        public void Summarize_NamesChangedAndMissingEntities()
        {
            var host = new NetworkCombatState
            {
                Units = [new NetworkUnit { Id = "hero", Position = new NetworkVector3(1, 0, 0), Descriptor = new NetworkUnitDescriptor { Damage = 2 } }],
                AreaEffects = [new NetworkAreaEffect { Id = "cloud" }]
            };
            var client = new NetworkCombatState
            {
                Units = [new NetworkUnit { Id = "hero", Position = new NetworkVector3(2, 0, 0), Descriptor = new NetworkUnitDescriptor { Damage = 3 } }]
            };

            Assert.That(RealTimeCombatDiff.Summarize(host, client), Is.EqualTo("unit:hero,missing-area:cloud"));
        }

        [Test]
        public void Summarize_IdentifiesProneAndEngagementMismatch()
        {
            var host = new NetworkCombatState
            {
                Units = [new NetworkUnit
                {
                    Id = "hero",
                    Descriptor = new NetworkUnitDescriptor
                    {
                        State = new NetworkUnitState { Prone = new NetworkUnitProneState { Active = true } }
                    },
                    CombatState = new NetworkUnitCombatState
                    {
                        EngagedUnits = [new NetworkUnitEngagement { UnitId = "enemy" }]
                    }
                }]
            };
            var client = new NetworkCombatState { Units = [new NetworkUnit { Id = "hero" }] };

            Assert.That(RealTimeCombatDiff.Summarize(host, client),
                Is.EqualTo("unit:hero:prone+engagement"));
        }

        [Test]
        public void Summarize_IdentifiesWrongEntityBlueprints()
        {
            var host = new NetworkCombatState
            {
                Units = [new NetworkUnit { Id = "summon", BlueprintId = "wolf" }],
                AreaEffects = [new NetworkAreaEffect { Id = "cloud", BlueprintId = "fog" }]
            };
            var client = new NetworkCombatState
            {
                Units = [new NetworkUnit { Id = "summon", BlueprintId = "bear" }],
                AreaEffects = [new NetworkAreaEffect { Id = "cloud", BlueprintId = "fire" }]
            };

            Assert.That(RealTimeCombatDiff.Summarize(host, client),
                Is.EqualTo("unit:summon:blueprint,area:cloud:blueprint"));
        }

        [Test]
        public void Summarize_IdentifiesAreaContextMismatch()
        {
            var host = new NetworkCombatState
            {
                AreaEffects = [new NetworkAreaEffect { Id = "cloud", CasterUnitId = "host-caster" }]
            };
            var client = new NetworkCombatState
            {
                AreaEffects = [new NetworkAreaEffect { Id = "cloud", CasterUnitId = "client-caster" }]
            };

            Assert.That(RealTimeCombatDiff.Summarize(host, client), Is.EqualTo("area:cloud:context"));
        }
    }
}
