using System;
using System.Linq;
using System.Reflection;
using Kingmaker.UnitLogic.Commands.Base;
using Kingmaker.Visual.Animation.Kingmaker;
using NUnit.Framework;
using WOTRMultiplayer.HarmonyPatches.Combat;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimePresentationTests
    {
        [Test]
        public void OutOfOrderFrames_AreAppliedOnceInOrder()
        {
            var inbox = new RealTimePresentationInbox();
            Assert.That(inbox.Enqueue(new NotifyRealTimeAttackPresentation { Sequence = 2 }), Is.True);
            Assert.That(inbox.TryPeek(out _), Is.False);
            Assert.That(inbox.Enqueue(new NotifyRealTimeAttackPresentation { Sequence = 1 }), Is.True);
            Assert.That(inbox.TryPeek(out var first), Is.True);
            Assert.That(first.Sequence, Is.EqualTo(1));
            inbox.Commit(first.Sequence);
            Assert.That(inbox.Enqueue(first), Is.True);
            Assert.That(inbox.TryPeek(out var second), Is.True);
            Assert.That(second.Sequence, Is.EqualTo(2));
            inbox.Commit(second.Sequence);
            Assert.That(inbox.TryPeek(out _), Is.False);
            Assert.That(inbox.AppliedSequence, Is.EqualTo(2));
        }

        [Test]
        public void UnappliedFrame_DoesNotAdvanceSequence()
        {
            var inbox = new RealTimePresentationInbox();
            inbox.Enqueue(new NotifyRealTimeAttackPresentation { Sequence = 1 });
            Assert.That(inbox.TryPeek(out _), Is.True);
            Assert.That(inbox.AppliedSequence, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => inbox.Commit(2));
            Assert.That(inbox.TryPeek(out var retry), Is.True);
            Assert.That(retry.Sequence, Is.EqualTo(1));
        }

        [Test]
        public void Gap_IsBoundedAndExpiresUntilEarlierFrameArrives()
        {
            var inbox = new RealTimePresentationInbox();
            Assert.That(inbox.Enqueue(new NotifyRealTimeAttackPresentation { Sequence = 129 }), Is.False);
            inbox.Enqueue(new NotifyRealTimeAttackPresentation { Sequence = 2 });
            Assert.That(inbox.HasExpiredGap(DateTime.UtcNow.AddSeconds(6)), Is.True);
            inbox.Enqueue(new NotifyRealTimeAttackPresentation { Sequence = 1 });
            inbox.Commit(1);
            Assert.That(inbox.HasExpiredGap(DateTime.UtcNow.AddSeconds(6)), Is.False);
        }

        [Test]
        public void ActualGame_HasExpectedPresentationPatchTargets()
        {
            Assert.That(RealTimePresentationControllerPatches.Targets().Count(), Is.EqualTo(3));
            Assert.That(typeof(UnitCommand).GetMethod("StartAnimation",
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(UnitAnimationActionHandle) }, null), Is.Not.Null);
            var rules = RealTimePresentationRulePatches.Targets().ToArray();
            Assert.That(rules.Any(method => method.DeclaringType.Name == "RuleAttackWithWeapon"), Is.True);
            Assert.That(rules.Any(method => method.DeclaringType.Name == "RuleDealDamage"), Is.True);
        }

        [Test]
        public void ReferenceState_DropsIncludedEventsAndResumesAtFollowingSequence()
        {
            var inbox = new RealTimePresentationInbox();
            inbox.Enqueue(new NotifyRealTimeAttackPresentation { Sequence = 2 });
            inbox.Enqueue(new NotifyRealTimeAttackPresentation { Sequence = 3 });
            inbox.RestoreBaseline(2);
            Assert.That(inbox.AppliedSequence, Is.EqualTo(2));
            Assert.That(inbox.TryPeek(out var next), Is.True);
            Assert.That(next.Sequence, Is.EqualTo(3));
        }

        [Test]
        public void ActionLifecycle_RemainsOrderedWhenNetworkDeliveryIsReordered()
        {
            var inbox = new RealTimePresentationInbox();
            inbox.Enqueue(new NotifyRealTimeAttackPresentation
                { Sequence = 3, ActionId = "a", Stage = (int)Entities.Combat.RealTime.RealTimePresentationStage.End });
            inbox.Enqueue(new NotifyRealTimeAttackPresentation
                { Sequence = 2, ActionId = "a", Stage = (int)Entities.Combat.RealTime.RealTimePresentationStage.Impact });
            inbox.Enqueue(new NotifyRealTimeAttackPresentation
                { Sequence = 1, ActionId = "a", Stage = (int)Entities.Combat.RealTime.RealTimePresentationStage.Start });
            var stages = new System.Collections.Generic.List<int>();
            while (inbox.TryPeek(out var frame))
            {
                stages.Add(frame.Stage);
                inbox.Commit(frame.Sequence);
            }
            Assert.That(stages, Is.EqualTo(new[]
            {
                (int)Entities.Combat.RealTime.RealTimePresentationStage.Start,
                (int)Entities.Combat.RealTime.RealTimePresentationStage.Impact,
                (int)Entities.Combat.RealTime.RealTimePresentationStage.End
            }));
        }

        [TestCase(-1, 0f)]
        [TestCase(0, 0f)]
        [TestCase(50, 0.5f)]
        [TestCase(100, 1f)]
        [TestCase(200, 1f)]
        public void VisualInterpolation_IsBoundedAndSmooth(int elapsedMilliseconds, float expected)
        {
            var started = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
            var progress = RealTimeVisualInterpolation.Progress(started,
                started.AddMilliseconds(elapsedMilliseconds), TimeSpan.FromMilliseconds(100));
            Assert.That(progress, Is.EqualTo(expected).Within(0.0001f));
        }
    }
}
