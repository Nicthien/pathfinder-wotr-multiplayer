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
    }
}
