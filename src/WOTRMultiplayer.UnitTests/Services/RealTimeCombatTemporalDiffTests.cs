using System;
using NUnit.Framework;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeCombatTemporalDiffTests
    {
        [Test]
        public void SmallTransitDelay_DoesNotReportDivergence()
        {
            var host = Sample();
            var client = Sample();
            client.Units[0].BuffCollection.Buffs[0].TimeLeft -= TimeSpan.FromMilliseconds(200);
            client.AreaEffects[0].CreationGameTimeTicks += TimeSpan.FromMilliseconds(200).Ticks;

            Assert.That(RealTimeCombatTemporalDiff.Summarize(host, client), Is.Null);
        }

        [Test]
        public void BuffAndAreaTimerDrift_AreIdentified()
        {
            var host = Sample();
            var client = Sample();
            client.Units[0].BuffCollection.Buffs[0].TimeLeft -= TimeSpan.FromSeconds(3);
            client.Units[0].BuffCollection.Buffs[0].NextTickTime += TimeSpan.FromSeconds(2);
            client.AreaEffects[0].DurationTicks += TimeSpan.FromSeconds(4).Ticks;

            Assert.That(RealTimeCombatTemporalDiff.Summarize(host, client),
                Is.EqualTo("area-duration:cloud,buff-next-tick:hero:haste,buff-time:hero:haste"));
        }

        [Test]
        public void PermanentAndTimedBuff_AreNeverEquivalent()
        {
            var host = Sample();
            var client = Sample();
            host.Units[0].BuffCollection.Buffs[0].TimeLeft = TimeSpan.MaxValue;
            client.Units[0].BuffCollection.Buffs[0].TimeLeft = TimeSpan.MaxValue - TimeSpan.FromMilliseconds(1);

            Assert.That(RealTimeCombatTemporalDiff.Summarize(host, client),
                Is.EqualTo("buff-time:hero:haste"));
        }

        [Test]
        public void TemporaryNegativeLevelDrift_IsIdentified()
        {
            var host = Sample();
            var client = Sample();
            host.Units[0].BuffCollection.NegativeLevels.Add(new NetworkUnitNegativeLevelsData
            {
                Count = 1, Duration = TimeSpan.FromMinutes(10)
            });
            client.Units[0].BuffCollection.NegativeLevels.Add(new NetworkUnitNegativeLevelsData
            {
                Count = 1, Duration = TimeSpan.FromMinutes(9)
            });
            Assert.That(RealTimeCombatTemporalDiff.Summarize(host, client), Is.EqualTo("negative-level-time:hero:Temporary:1"));
        }

        [Test]
        public void NegativeLevelTransitDelayAndOrdering_DoNotReportDivergence()
        {
            var host = Sample();
            var client = Sample();
            host.Units[0].BuffCollection.NegativeLevels.Add(new NetworkUnitNegativeLevelsData
            {
                Count = 1, Duration = TimeSpan.FromMinutes(10)
            });
            host.Units[0].BuffCollection.NegativeLevels.Add(new NetworkUnitNegativeLevelsData
            {
                Count = 1, Duration = TimeSpan.FromMinutes(5)
            });
            client.Units[0].BuffCollection.NegativeLevels.Add(new NetworkUnitNegativeLevelsData
            {
                Count = 1, Duration = TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(200)
            });
            client.Units[0].BuffCollection.NegativeLevels.Add(new NetworkUnitNegativeLevelsData
            {
                Count = 1, Duration = TimeSpan.FromMinutes(10) - TimeSpan.FromMilliseconds(200)
            });
            Assert.That(RealTimeCombatTemporalDiff.Summarize(host, client), Is.Null);
        }

        private static NetworkCombatState Sample() => new()
        {
            Units = [new NetworkUnit
            {
                Id = "hero",
                BuffCollection = new NetworkUnitBuffCollection
                {
                    Buffs = [new NetworkBuff
                    {
                        Id = "haste", TimeLeft = TimeSpan.FromSeconds(20),
                        NextTickTime = TimeSpan.FromSeconds(6),
                        NextResourceSpendingTime = TimeSpan.FromSeconds(10)
                    }]
                }
            }],
            AreaEffects = [new NetworkAreaEffect
            {
                Id = "cloud", CreationGameTimeTicks = TimeSpan.FromMinutes(5).Ticks,
                DurationTicks = TimeSpan.FromSeconds(30).Ticks
            }]
        };
    }
}
