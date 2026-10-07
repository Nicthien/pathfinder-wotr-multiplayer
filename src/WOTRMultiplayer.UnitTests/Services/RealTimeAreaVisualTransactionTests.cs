using System;
using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeAreaVisualTransactionTests
    {
        private sealed class Visual : IDisposable
        {
            public int Disposals;
            public bool Fail;
            public void Dispose() { Disposals++; if (Fail) throw new InvalidOperationException("cleanup"); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void VisualOwnershipFollowsCheckpointCommit(bool commit)
        {
            var created = new Visual(); var removed = new Visual();
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                transaction.TrackAreaVisual(created, removed: false);
                transaction.TrackAreaVisual(removed, removed: true);
                Assert.That(transaction.HasChanges, Is.True);
                if (commit) transaction.VerifyAndCommit(new NetworkCombatState(), new NetworkCombatState());
            }
            Assert.That(created.Disposals, Is.EqualTo(commit ? 0 : 1));
            Assert.That(removed.Disposals, Is.EqualTo(commit ? 1 : 0));
        }

        [Test]
        public void DuplicateOwnershipIsRejected()
        {
            using var transaction = new RealTimeBuffRestoreTransaction(); var visual = new Visual();
            transaction.TrackAreaVisual(visual, removed: false);
            Assert.Throws<InvalidOperationException>(() => transaction.TrackAreaVisual(visual, removed: true));
        }

        [Test]
        public void FailedCheckpointDestroysNewVisualAndKeepsOldVisual()
        {
            var created = new Visual(); var removed = new Visual();
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                transaction.TrackAreaVisual(created, removed: false);
                transaction.TrackAreaVisual(removed, removed: true);
                var wrong = new NetworkCombatState();
                wrong.AreaEffects.Add(RealTimeAreaReplicaFactoryTests.Source().State);
                Assert.Throws<InvalidOperationException>(() => transaction.VerifyAndCommit(new NetworkCombatState(), wrong));
            }
            Assert.That(created.Disposals, Is.EqualTo(1)); Assert.That(removed.Disposals, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CleanupFailureDoesNotSkipOtherViews(bool commit)
        {
            var transaction = new RealTimeBuffRestoreTransaction(); var good = new Visual(); var bad = new Visual { Fail = true };
            transaction.TrackAreaVisual(good, removed: commit); transaction.TrackAreaVisual(bad, removed: commit);
            if (commit) transaction.VerifyAndCommit(new NetworkCombatState(), new NetworkCombatState());
            Assert.Throws<AggregateException>(() => transaction.Dispose());
            Assert.That(good.Disposals, Is.EqualTo(1)); Assert.That(bad.Disposals, Is.EqualTo(1));
            Assert.DoesNotThrow(() => transaction.Dispose());
        }
    }
}
