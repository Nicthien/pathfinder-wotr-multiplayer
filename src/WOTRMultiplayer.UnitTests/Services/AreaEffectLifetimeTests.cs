using NUnit.Framework;
using System;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class AreaEffectLifetimeTests
    {
        [TestCase(100L, 60L, 120L, 40L)]
        [TestCase(100L, 60L, 200L, 0L)]
        [TestCase(long.MaxValue, long.MaxValue, 0L, long.MaxValue)]
        public void RemainingLifetimeUsesTheHostClockAndDoesNotOverflow(long created, long duration, long now, long expected)
            => Assert.That(RealTimeAreaReplication.RemainingTicks(created, duration, now), Is.EqualTo(expected));

        [Test]
        public void UnlimitedLifetimeRemainsUnlimited()
            => Assert.That(RealTimeAreaReplication.RemainingTicks(1, null, 2), Is.Null);

        [Test]
        public void RemainingLifetimeIsAnchoredToTheClientClock()
        {
            var area = new AreaEffectEntityData(default(JsonConstructorMark));
            RealTimeAreaReplication.ApplyRemainingDuration(area, 40, TimeSpan.FromTicks(1000));
            Assert.That(((TimeSpan)HarmonyLib.AccessTools.Field(typeof(AreaEffectEntityData), "m_CreationTime").GetValue(area)).Ticks, Is.EqualTo(1000));
            Assert.That(((TimeSpan?)HarmonyLib.AccessTools.Field(typeof(AreaEffectEntityData), "m_Duration").GetValue(area))?.Ticks, Is.EqualTo(40));
        }

        [Test]
        public void RemainingLifetimeIsCappedBeforeTimeSpanAdditionCanOverflow()
        {
            var area = new AreaEffectEntityData(default(JsonConstructorMark));
            RealTimeAreaReplication.ApplyRemainingDuration(area, long.MaxValue, TimeSpan.FromTicks(1000));
            var created = (TimeSpan)HarmonyLib.AccessTools.Field(typeof(AreaEffectEntityData), "m_CreationTime").GetValue(area);
            var duration = (TimeSpan?)HarmonyLib.AccessTools.Field(typeof(AreaEffectEntityData), "m_Duration").GetValue(area);
            Assert.That(created + duration, Is.EqualTo(TimeSpan.MaxValue));
        }
    }
}
