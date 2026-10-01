using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeCombatEntityPreflightTests
    {
        [Test]
        public void ExistingEntities_PermitCheckpoint()
        {
            var checkpoint = new NetworkCombatState
            {
                Units = [CompleteUnit("summon")],
                AreaEffects = [new NetworkAreaEffect { Id = "cloud", BlueprintId = "cloud-type", Position = new NetworkVector3() }]
            };

            Assert.That(RealTimeCombatEntityPreflight.FindMissing(checkpoint,
                id => id == "summon", area => area.Id == "cloud"), Is.Null);
        }

        [Test]
        public void MissingSummonAndArea_AreReportedBeforeCheckpointIsApplied()
        {
            var checkpoint = new NetworkCombatState
            {
                Units = [new NetworkUnit { Id = "summon" }],
                AreaEffects = [new NetworkAreaEffect { Id = "cloud" }]
            };

            Assert.That(RealTimeCombatEntityPreflight.FindMissing(checkpoint,
                _ => false, _ => false), Is.EqualTo("missing-area:cloud,missing-unit:summon"));
        }

        [Test]
        public void MissingAreaOccupantAndEngagementTarget_BlockCheckpointBeforeMutation()
        {
            var checkpoint = new NetworkCombatState
            {
                Units = [new NetworkUnit
                {
                    Id = "hero",
                    BlueprintId = "hero-type",
                    Position = new NetworkVector3(),
                    Descriptor = new NetworkUnitDescriptor
                    {
                        Stats = new NetworkCharacterStats(),
                        State = new NetworkUnitState { Prone = new NetworkUnitProneState() }
                    },
                    BuffCollection = new NetworkUnitBuffCollection(),
                    CombatState = new NetworkUnitCombatState
                    {
                        EngagedUnits = [new NetworkUnitEngagement { UnitId = "missing-enemy" }]
                    }
                }],
                AreaEffects = [new NetworkAreaEffect
                {
                    Id = "cloud",
                    BlueprintId = "cloud-type",
                    Position = new NetworkVector3(),
                    UnitsInside = ["missing-summon"]
                }]
            };

            Assert.That(RealTimeCombatEntityPreflight.FindMissing(checkpoint,
                id => id == "hero", area => area.Id == "cloud"),
                Is.EqualTo("missing-area-unit:cloud:missing-summon,missing-engagement-target:hero:missing-enemy"));
        }

        [Test]
        public void RepeatedMissingReferences_AreReportedOnce()
        {
            var checkpoint = new NetworkCombatState
            {
                AreaEffects = [new NetworkAreaEffect { Id = "cloud", BlueprintId = "cloud-type", Position = new NetworkVector3(), UnitsInside = ["missing", "missing"] }]
            };

            Assert.That(RealTimeCombatEntityPreflight.FindMissing(checkpoint,
                _ => false, area => area.Id == "cloud"),
                Is.EqualTo("missing-area-unit:cloud:missing"));
        }

        [Test]
        public void IncompleteExistingEntities_AreRejectedBeforeCheckpointMutation()
        {
            var checkpoint = new NetworkCombatState
            {
                Units = [new NetworkUnit { Id = "hero" }],
                AreaEffects = [new NetworkAreaEffect { Id = "cloud", UnitsInside = null }]
            };

            Assert.That(RealTimeCombatEntityPreflight.FindMissing(checkpoint,
                id => id == "hero", area => area.Id == "cloud"),
                Is.EqualTo("invalid-area:cloud:blueprint,invalid-area:cloud:occupants,invalid-area:cloud:position,invalid-unit:hero:blueprint,invalid-unit:hero:buffs,invalid-unit:hero:position,invalid-unit:hero:state,invalid-unit:hero:stats"));
        }

        [Test]
        public void MissingTopLevelCollections_AreRejectedBeforeCheckpointMutation()
        {
            var checkpoint = new NetworkCombatState { Units = null, AreaEffects = null };

            Assert.That(RealTimeCombatEntityPreflight.FindMissing(checkpoint,
                _ => true, _ => true), Is.EqualTo("invalid-combat:areas,invalid-combat:units"));
        }

        [Test]
        public void NullBuffAndEngagementEntries_AreRejectedBeforeMutation()
        {
            var unit = CompleteUnit("hero");
            unit.BuffCollection.Buffs.Add(null);
            unit.CombatState = new NetworkUnitCombatState
            {
                EngagedUnits = [null],
                EngagedBy = null
            };
            var checkpoint = new NetworkCombatState { Units = [unit] };

            Assert.That(RealTimeCombatEntityPreflight.FindMissing(checkpoint,
                id => id == "hero", _ => true),
                Is.EqualTo("invalid-unit:hero:buff-entry,invalid-unit:hero:engagements,missing-engagement-target:hero:<null>"));
        }

        [Test]
        public void ExistingAreaWithoutView_IsRejectedBeforeMutation()
        {
            var checkpoint = new NetworkCombatState
            {
                Units = [CompleteUnit("hero")],
                AreaEffects = [new NetworkAreaEffect { Id = "cloud", BlueprintId = "cloud-type", Position = new NetworkVector3() }]
            };

            Assert.That(RealTimeCombatEntityPreflight.FindMissing(checkpoint,
                _ => true, _ => true, _ => false),
                Is.EqualTo("invalid-area:cloud:view"));
        }

        [Test]
        public void WrongLocalBlueprints_AreRejectedBeforeMutation()
        {
            var checkpoint = new NetworkCombatState
            {
                Units = [CompleteUnit("hero")],
                AreaEffects = [new NetworkAreaEffect
                {
                    Id = "cloud", BlueprintId = "cloud-type", Position = new NetworkVector3()
                }]
            };

            Assert.That(RealTimeCombatEntityPreflight.FindMissing(checkpoint,
                _ => true, _ => true, null, _ => "wrong-unit-type", _ => "wrong-area-type"),
                Is.EqualTo("invalid-area:cloud:blueprint,invalid-unit:hero:blueprint"));
        }

        [Test]
        public void WrongLocalScenes_AreRejectedBeforeMutation()
        {
            var unit = CompleteUnit("hero");
            unit.SceneName = "main-scene";
            var checkpoint = new NetworkCombatState
            {
                Units = [unit],
                AreaEffects = [new NetworkAreaEffect
                {
                    Id = "cloud", BlueprintId = "cloud-type", Position = new NetworkVector3(),
                    SceneName = "main-scene"
                }]
            };

            Assert.That(RealTimeCombatEntityPreflight.FindMissing(checkpoint,
                _ => true, _ => true, null, null, null,
                _ => "other-scene", _ => "other-scene"),
                Is.EqualTo("invalid-area:cloud:scene,invalid-unit:hero:scene"));
        }

        [Test]
        public void DuplicateEntityIds_AreRejectedBeforeMutation()
        {
            var checkpoint = new NetworkCombatState
            {
                Units = [CompleteUnit("hero"), CompleteUnit("HERO")],
                AreaEffects =
                [
                    new NetworkAreaEffect { Id = "cloud", BlueprintId = "cloud-type", Position = new NetworkVector3() },
                    new NetworkAreaEffect { Id = "CLOUD", BlueprintId = "cloud-type", Position = new NetworkVector3() }
                ]
            };

            Assert.That(RealTimeCombatEntityPreflight.FindMissing(checkpoint,
                _ => true, _ => true),
                Is.EqualTo("duplicate-area:CLOUD,duplicate-unit:HERO"));
        }

        [Test]
        public void InvalidCoordinatesAndOrientation_AreRejectedBeforeMutation()
        {
            var hero = CompleteUnit("hero");
            hero.Position.X = float.NaN;
            hero.Orientation = float.PositiveInfinity;
            var checkpoint = new NetworkCombatState
            {
                Units = [hero],
                AreaEffects = [new NetworkAreaEffect
                {
                    Id = "cloud", BlueprintId = "cloud-type",
                    Position = new NetworkVector3(0, float.NegativeInfinity, 0)
                }]
            };

            Assert.That(RealTimeCombatEntityPreflight.FindMissing(checkpoint,
                _ => true, _ => true),
                Is.EqualTo("invalid-area:cloud:position,invalid-unit:hero:orientation,invalid-unit:hero:position"));
        }

        private static NetworkUnit CompleteUnit(string id) => new NetworkUnit
        {
            Id = id,
            BlueprintId = "hero-type",
            Position = new NetworkVector3(),
            Descriptor = new NetworkUnitDescriptor
            {
                Stats = new NetworkCharacterStats(),
                State = new NetworkUnitState { Prone = new NetworkUnitProneState() }
            },
            BuffCollection = new NetworkUnitBuffCollection()
        };
    }
}
