using System;
using NUnit.Framework;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimePresentationLifetimeTests
    {
        [Test]
        public void PausedLogicalTimeNeverConsumesBudgetRegardlessOfTickCount()
        {
            var started = TimeSpan.FromSeconds(100);
            var lifetime = new RealTimePresentationLifetime(started);
            for (var tick = 0; tick < 10000; tick++) Assert.That(lifetime.FailureAt(started), Is.Null);
            Assert.That(lifetime.FailureAt(started + TimeSpan.FromSeconds(14)), Is.Null);
            Assert.That(lifetime.FailureAt(started + TimeSpan.FromSeconds(15)),
                Is.EqualTo("presentation-action-terminal-event-timeout"));
        }

        [TestCase(0f)][TestCase(1.5f)][TestCase(30f)][TestCase(600f)]
        public void CastingDurationIsAllowedBeforeTerminalGracePeriod(float castingTime)
        {
            var started = TimeSpan.FromDays(100);
            var lifetime = new RealTimePresentationLifetime(started, castingTime);
            Assert.That(lifetime.Budget.TotalSeconds, Is.EqualTo((double)castingTime + 15));
            Assert.That(lifetime.FailureAt(started + lifetime.Budget - TimeSpan.FromTicks(1)), Is.Null);
            Assert.That(lifetime.FailureAt(started + lifetime.Budget),
                Is.EqualTo("presentation-action-terminal-event-timeout"));
        }

        [TestCase(-1f)][TestCase(float.NaN)][TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)][TestCase(float.MaxValue)]
        public void InvalidOrUnrepresentableCastingDurationIsRejected(float duration)
            => Assert.Throws<ArgumentOutOfRangeException>(() => new RealTimePresentationLifetime(TimeSpan.Zero, duration));

        [TestCase(0.1f)][TestCase(0.5f)][TestCase(1f)][TestCase(20f)]
        public void SlowAnimationHasAnExtendedBudgetAndFastAnimationKeepsGrace(float speed)
        {
            var lifetime = new RealTimePresentationLifetime(TimeSpan.Zero, 30, speed);
            Assert.That(lifetime.Budget.TotalSeconds, Is.EqualTo(45d / Math.Min((double)speed, 1d)).Within(0.001));
            Assert.That(lifetime.FailureAt(TimeSpan.FromSeconds(44)), Is.Null);
        }

        [TestCase(0f)][TestCase(-1f)][TestCase(21f)][TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)][TestCase(float.Epsilon)]
        public void InvalidOrUnrepresentableSpeedCannotCreateAWatchdog(float speed)
            => Assert.Throws<ArgumentOutOfRangeException>(() => new RealTimePresentationLifetime(TimeSpan.Zero, 0, speed));

        [Test]
        public void ChangedGameTimelineRequiresProtectedRecoveryInsteadOfANegativeAge()
        {
            var lifetime = new RealTimePresentationLifetime(TimeSpan.FromSeconds(100));
            Assert.That(lifetime.FailureAt(TimeSpan.FromSeconds(99)), Is.EqualTo("presentation-game-clock-regressed"));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RealTimePresentationLifetime(TimeSpan.FromTicks(-1)));
        }

        [Test]
        public void NearMaximumLogicalTimeDoesNotOverflowDeadlineArithmetic()
        {
            var lifetime = new RealTimePresentationLifetime(TimeSpan.MaxValue - TimeSpan.FromSeconds(1));
            Assert.That(lifetime.FailureAt(TimeSpan.MaxValue), Is.Null);
        }
    }
}
