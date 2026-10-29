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

            Assert.That(RealTimeCombatDiff.Summarize(host, client), Is.EqualTo("unit:hero:position+damage,missing-area:cloud"));
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

        [Test]
        public void Summarize_IdentifiesAreaTargetMismatch()
        {
            var host = new NetworkCombatState
            {
                AreaEffects = [new NetworkAreaEffect { Id = "cloud", TargetUnitId = "hero", IsAttachedToUnit = true }]
            };
            var client = new NetworkCombatState
            {
                AreaEffects = [new NetworkAreaEffect { Id = "cloud", TargetUnitId = "other" }]
            };

            Assert.That(RealTimeCombatDiff.Summarize(host, client), Is.EqualTo("area:cloud:target"));
        }

        [Test]
        public void Summarize_IdentifiesSummonOriginMismatch()
        {
            var host = new NetworkCombatState
            {
                Units = [new NetworkUnit { Id = "summon", MasterUnitId = "host-master" }]
            };
            var client = new NetworkCombatState
            {
                Units = [new NetworkUnit { Id = "summon", MasterUnitId = "client-master" }]
            };

            Assert.That(RealTimeCombatDiff.Summarize(host, client), Is.EqualTo("unit:summon:origin"));
        }

        [Test]
        public void Summarize_IdentifiesWrongEntityScenes()
        {
            var host = new NetworkCombatState
            {
                Units = [new NetworkUnit { Id = "summon", SceneName = "main-scene" }],
                AreaEffects = [new NetworkAreaEffect { Id = "cloud", SceneName = "main-scene" }]
            };
            var client = new NetworkCombatState
            {
                Units = [new NetworkUnit { Id = "summon", SceneName = "other-scene" }],
                AreaEffects = [new NetworkAreaEffect { Id = "cloud", SceneName = "other-scene" }]
            };

            Assert.That(RealTimeCombatDiff.Summarize(host, client),
                Is.EqualTo("unit:summon:scene,area:cloud:scene"));
        }

        [Test]
        public void Summarize_IdentifiesBuffsAndAreaOccupants()
        {
            var host = new NetworkCombatState
            {
                Units = [new NetworkUnit { Id = "hero", BuffCollection = new NetworkUnitBuffCollection
                {
                    Buffs = [new NetworkBuff { Id = "bless", Rank = 2 }]
                } }],
                AreaEffects = [new NetworkAreaEffect { Id = "cloud", UnitsInside = ["hero"] }]
            };
            var client = new NetworkCombatState
            {
                Units = [new NetworkUnit { Id = "hero", BuffCollection = new NetworkUnitBuffCollection() }],
                AreaEffects = [new NetworkAreaEffect { Id = "cloud" }]
            };

            Assert.That(RealTimeCombatDiff.Summarize(host, client),
                Is.EqualTo("unit:hero:buffs,area:cloud:occupants"));
        }

        [Test]
        public void Summarize_ReportsDuplicateIdsAndInvalidCoordinatesWithoutThrowing()
        {
            var host = new NetworkCombatState
            {
                Units = [new NetworkUnit { Id = "hero", Position = new NetworkVector3() }]
            };
            var client = new NetworkCombatState
            {
                Units =
                [
                    new NetworkUnit { Id = "hero", Position = new NetworkVector3(float.NaN, 0, 0) },
                    new NetworkUnit { Id = "hero" }
                ]
            };

            Assert.That(RealTimeCombatDiff.Summarize(host, client),
                Is.EqualTo("duplicate-client-unit:hero,unit:hero:position"));
        }

        [Test]
        public void Summarize_ReportsExactVitalDifference()
        {
            static NetworkStatValues Stat(int value) => new()
            {
                BaseValue = value, ModifiedValue = value,
                ModifiedValueRaw = value, PermanentValue = value
            };
            static NetworkUnit Unit(int hp, int group) => new()
            {
                Id = "enemy",
                Descriptor = new NetworkUnitDescriptor
                {
                    Vitals = new NetworkUnitVitals
                    {
                        HitPoints = Stat(hp), TemporaryHitPoints = Stat(0), NonLethal = Stat(0),
                        GroupCounterCaptured = true, GroupCombatCounter = group
                    }
                }
            };

            var result = RealTimeCombatDiff.Summarize(
                new NetworkCombatState { Units = [Unit(12, 2)] },
                new NetworkCombatState { Units = [Unit(0, 1)] });

            Assert.That(result, Is.EqualTo("unit:enemy:vitals[hp,group=2/1]"));
        }
    }
}
