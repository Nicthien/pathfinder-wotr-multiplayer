using System;
using System.Linq;
using NUnit.Framework;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Networking.Messages.Contracts;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimePresentationSnapshotBarrierTests
    {
        private static NotifyRealTimeCombatSnapshot Snapshot(long sequence = 4, long included = 2) => new()
        {
            SessionId = "session", LoadedSaveSeed = 7, Epoch = 3, Sequence = sequence,
            IncludedEventSequence = included, State = new NetworkCombatState()
        };
        private static RealTimePresentationSnapshotBarrier Barrier() => new("session", 7, 3);

        [Test]
        public void SnapshotWaitsForActuallyAppliedEventsAndCannotAdvanceTheirCursor()
        {
            var barrier = Barrier();
            Assert.That(barrier.Offer(Snapshot(), 0, DateTime.UtcNow), Is.Null);
            Assert.That(barrier.TakeReady(0), Is.Null);
            Assert.That(barrier.TakeReady(1), Is.Null);
            Assert.That(barrier.TakeReady(2).Sequence, Is.EqualTo(4));
            Assert.That(barrier.TakeReady(2), Is.Null);
        }

        [Test]
        public void MatchingBoundaryCanApplyWithoutStoppingAnimations()
        {
            var snapshot = Snapshot();
            Assert.That(Barrier().Offer(snapshot, 2, DateTime.UtcNow), Is.SameAs(snapshot));
        }

        [Test]
        public void OlderCheckpointCannotOverwriteAlreadyAppliedResults()
        {
            var barrier = Barrier();
            Assert.That(barrier.Offer(Snapshot(), 3, DateTime.UtcNow), Is.Null);
            Assert.That(barrier.TakeReady(2), Is.Null);
            barrier.Offer(Snapshot(), 0, DateTime.UtcNow);
            Assert.That(barrier.TakeReady(3), Is.Null);
            Assert.That(barrier.TakeReady(2), Is.Null);
        }

        [Test]
        public void KeepsOnlyLatestSnapshotAndOwnsTheWaitingPayload()
        {
            var barrier = Barrier(); var now = DateTime.UtcNow;
            barrier.Offer(Snapshot(), 0, now);
            var latest = Snapshot(5, 3);
            latest.State.RoundNumber = 99;
            barrier.Offer(latest, 0, now);
            latest.State.RoundNumber = 0; latest.IncludedEventSequence = 100;
            barrier.Offer(Snapshot(3, 1), 0, now);
            Assert.That(barrier.TakeReady(2), Is.Null);
            var ready = barrier.TakeReady(3);
            Assert.That(ready.Sequence, Is.EqualTo(5));
            Assert.That(ready.State.RoundNumber, Is.EqualTo(99));
        }

        [Test]
        public void DuplicateIsIdempotentButConflictingPayloadIsRejected()
        {
            var barrier = Barrier();
            barrier.Offer(Snapshot(), 0, DateTime.UtcNow);
            Assert.That(barrier.Offer(Snapshot(), 0, DateTime.UtcNow), Is.Null);
            var conflict = Snapshot(); conflict.State.RoundNumber = 1;
            Assert.Throws<InvalidOperationException>(() => barrier.Offer(conflict, 0, DateTime.UtcNow));
            Assert.That(barrier.TakeReady(2).State.RoundNumber, Is.Zero);
        }

        [Test]
        public void NewerSnapshotCannotRegressItsEventBoundary()
        {
            var barrier = Barrier(); barrier.Offer(Snapshot(), 0, DateTime.UtcNow);
            Assert.Throws<InvalidOperationException>(() => barrier.Offer(Snapshot(5, 1), 0, DateTime.UtcNow));
            Assert.That(barrier.TakeReady(2).Sequence, Is.EqualTo(4));
        }

        [Test]
        public void NewSnapshotsAndDuplicatesDoNotExtendAnUnresolvedWait()
        {
            var barrier = Barrier(); var now = DateTime.UtcNow;
            barrier.Offer(Snapshot(), 0, now);
            barrier.Offer(Snapshot(), 0, now.AddSeconds(4));
            barrier.Offer(Snapshot(5, 3), 0, now.AddSeconds(4));
            Assert.That(barrier.HasExpiredWait(now.AddSeconds(4)), Is.False);
            Assert.That(barrier.HasExpiredWait(now.AddSeconds(6)), Is.True);
            Assert.That(barrier.TakeReady(3), Is.Not.Null);
            Assert.That(barrier.HasExpiredWait(now.AddSeconds(7)), Is.False);
        }

        [TestCase("session")][TestCase("save")][TestCase("epoch")][TestCase("sequence")]
        [TestCase("included")][TestCase("applied")][TestCase("state")][TestCase("units")]
        [TestCase("areas")][TestCase("projectiles")][TestCase("units-count")]
        public void InvalidOrForeignSnapshotIsRejectedWithoutReplacingThePendingState(string fault)
        {
            var barrier = Barrier(); barrier.Offer(Snapshot(), 0, DateTime.UtcNow);
            var snapshot = Snapshot(5, 3);
            switch (fault)
            {
                case "session": snapshot.SessionId = "other"; break;
                case "save": snapshot.LoadedSaveSeed++; break;
                case "epoch": snapshot.Epoch++; break;
                case "sequence": snapshot.Sequence = 0; break;
                case "included": snapshot.IncludedEventSequence = -1; break;
                case "state": snapshot.State = null; break;
                case "units": snapshot.State.Units = null; break;
                case "areas": snapshot.State.AreaEffects = null; break;
                case "projectiles": snapshot.State.Projectiles = null; break;
                case "units-count": snapshot.State.Units = Enumerable.Repeat(new NetworkUnit(), 4097).ToList(); break;
            }
            Assert.Throws<InvalidOperationException>(() => barrier.Offer(snapshot, fault == "applied" ? -1 : 0, DateTime.UtcNow));
            Assert.That(barrier.TakeReady(2).Sequence, Is.EqualTo(4));
        }
    }
}
