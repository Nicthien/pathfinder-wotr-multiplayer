using System;
using NUnit.Framework;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeBuffIdentityPlanTests
    {
        private static NetworkBuff Buff(string id, string caster = "caster") => new()
        { Id = id, BlueprintId = "blueprint", CasterId = caster, Rank = 1 };
        [Test]
        public void BridgesSingleNativeEffectWithoutChangingInput()
        {
            var local = Buff("local"); var host = Buff("host");
            var plan = RealTimeBuffIdentityPlan.Create(new[] { host }, new[] { local });
            Assert.That(plan["local"], Is.EqualTo("host"));
            Assert.That(local.Id, Is.EqualTo("local"));
        }
        [Test]
        public void NeverMergesAmbiguousStacksOrDifferentCasters()
        {
            Assert.That(RealTimeBuffIdentityPlan.Create(new[] { Buff("a"), Buff("b") }, new[] { Buff("c"), Buff("d") }), Is.Empty);
            Assert.That(RealTimeBuffIdentityPlan.Create(new[] { Buff("a") }, new[] { Buff("b", "other") }), Is.Empty);
            Assert.That(RealTimeBuffIdentityPlan.Create(new[] { Buff("a") }, new[] { Buff("a"), Buff("extra") }), Is.Empty);
        }
        [Test]
        public void MatchedStackLeavesOnlyUniqueUnmatchedEffect()
        {
            var plan = RealTimeBuffIdentityPlan.Create(new[] { Buff("a"), Buff("b") }, new[] { Buff("a"), Buff("c") });
            Assert.That(plan.Count, Is.EqualTo(1)); Assert.That(plan["c"], Is.EqualTo("b"));
        }
        [Test]
        public void CaptureScopeUnwindsAfterFailureWithoutLeakingIntoTurnBased()
        {
            Assert.That(RealTimeCaptureScope.Active, Is.False);
            using (var outer = new RealTimeCaptureScope())
            {
                Assert.Throws<InvalidOperationException>(() => { using var inner = new RealTimeCaptureScope(); throw new InvalidOperationException(); });
                Assert.That(RealTimeCaptureScope.Active, Is.True);
            }
            Assert.That(RealTimeCaptureScope.Active, Is.False);
        }
    }
}
