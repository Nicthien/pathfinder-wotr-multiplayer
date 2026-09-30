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
    }
}
