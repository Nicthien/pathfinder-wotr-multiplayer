using NUnit.Framework;
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
                Units = [new NetworkUnit { Id = "summon" }],
                AreaEffects = [new NetworkAreaEffect { Id = "cloud" }]
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
                    CombatState = new NetworkUnitCombatState
                    {
                        EngagedUnits = [new NetworkUnitEngagement { UnitId = "missing-enemy" }]
                    }
                }],
                AreaEffects = [new NetworkAreaEffect
                {
                    Id = "cloud",
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
                AreaEffects = [new NetworkAreaEffect { Id = "cloud", UnitsInside = ["missing", "missing"] }]
            };

            Assert.That(RealTimeCombatEntityPreflight.FindMissing(checkpoint,
                _ => false, area => area.Id == "cloud"),
                Is.EqualTo("missing-area-unit:cloud:missing"));
        }
    }
}
