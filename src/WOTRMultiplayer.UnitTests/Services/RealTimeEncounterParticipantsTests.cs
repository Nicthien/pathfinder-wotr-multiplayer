using System;
using System.Linq;
using NUnit.Framework;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeEncounterParticipantsTests
    {
        private sealed class Unit { public bool InCombat = true; public bool Dead; }

        [Test]
        public void NativeCheckpointRebindsDeadAndWithdrawnUnitsWithoutRetainingOldObjects()
        {
            var participants = RealTimeEncounterParticipants<Unit>.For(new object());
            var old = new Unit(); participants.Remember("dead", old);
            var dead = new Unit { InCombat = false, Dead = true };
            var withdrawn = new Unit { InCombat = false };
            using (var binding = participants.RebindCheckpoint(new[] { "dead", "withdrawn" },
                id => id == "dead" ? dead : withdrawn, () => true)) binding.Commit();
            Assert.That(participants.Snapshot()["dead"], Is.SameAs(dead));
            Assert.That(participants.Snapshot()["withdrawn"], Is.SameAs(withdrawn));
            participants.Remember("dead", dead);
            Assert.Throws<InvalidOperationException>(() => participants.Remember("dead", old));
        }

        [Test]
        public void UncommittedRebindRollsBackWholeRegistry()
        {
            var participants = RealTimeEncounterParticipants<Unit>.For(new object());
            var old = new Unit(); participants.Remember("old", old);
            using (participants.RebindCheckpoint(new[] { "new" }, _ => new Unit(), () => true))
                Assert.That(participants.Snapshot().ContainsKey("old"), Is.False);
            Assert.That(participants.Snapshot()["old"], Is.SameAs(old));
            Assert.That(participants.Snapshot().Count, Is.EqualTo(1));
        }

        [TestCase("duplicate")]
        [TestCase("duplicate-case")]
        [TestCase("blank")]
        [TestCase("missing")]
        [TestCase("revoked")]
        [TestCase("limit")]
        public void InvalidNativeRegistryCannotPublishPartialReplacement(string problem)
        {
            var participants = RealTimeEncounterParticipants<Unit>.For(new object());
            var old = new Unit(); participants.Remember("old", old);
            var ids = problem == "limit" ? System.Linq.Enumerable.Range(0, 4097).Select(index => index.ToString())
                : problem == "duplicate" ? new[] { "new", "new" } : problem == "duplicate-case" ? new[] { "new", "NEW" }
                : problem == "blank" ? new[] { "new", " " } : new[] { "new" };
            Assert.Throws<InvalidOperationException>(() => participants.RebindCheckpoint(ids,
                _ => problem == "missing" ? null : new Unit(), () => problem != "revoked"));
            Assert.That(participants.Snapshot()["old"], Is.SameAs(old));
            Assert.That(participants.Snapshot().Count, Is.EqualTo(1));
        }

        [Test]
        public void ConcurrentParticipantCannotBeLostWhileResolvingNativeRegistry()
        {
            var participants = RealTimeEncounterParticipants<Unit>.For(new object());
            var extra = new Unit();
            Assert.Throws<InvalidOperationException>(() => participants.RebindCheckpoint(new[] { "new" }, _ =>
            { participants.Remember("concurrent", extra); return new Unit(); }, () => true));
            Assert.That(participants.Snapshot()["concurrent"], Is.SameAs(extra));
            Assert.That(participants.Snapshot().ContainsKey("new"), Is.False);
        }

        [Test]
        public void RollbackConflictCannotOverwriteNewerRegistryOwner()
        {
            var participants = RealTimeEncounterParticipants<Unit>.For(new object());
            var binding = participants.RebindCheckpoint(new[] { "new" }, _ => new Unit(), () => true);
            var extra = new Unit(); participants.Remember("concurrent", extra);
            Assert.Throws<InvalidOperationException>(() => binding.Dispose());
            Assert.That(participants.Snapshot()["concurrent"], Is.SameAs(extra));
            binding.Dispose();
        }

        [Test]
        public void DeathAndWithdrawalRemainInReferenceAfterLeavingLiveCombatList()
        {
            var participants = RealTimeEncounterParticipants<Unit>.For(new object());
            var dead = new Unit();
            var withdrawn = new Unit();
            participants.Remember("dead", dead);
            participants.Remember("withdrawn", withdrawn);
            dead.InCombat = false; dead.Dead = true;
            withdrawn.InCombat = false;
            var reference = participants.Snapshot();
            Assert.That(reference.Count, Is.EqualTo(2));
            Assert.That(reference["dead"].Dead, Is.True);
            Assert.That(reference["dead"].InCombat, Is.False);
            Assert.That(reference["withdrawn"], Is.SameAs(withdrawn));
        }

        [Test]
        public void NewEncounterOrLoadDoesNotInheritOldParticipants()
        {
            var previous = RealTimeEncounterParticipants<Unit>.For(new object());
            previous.Remember("old-corpse", new Unit { Dead = true });
            var next = RealTimeEncounterParticipants<Unit>.For(new object());
            Assert.That(next.Snapshot(), Is.Empty);
        }

        [Test]
        public void SameEncounterSurvivesRepairAndDuplicateRegistration()
        {
            var encounter = new object();
            var unit = new Unit();
            RealTimeEncounterParticipants<Unit>.For(encounter).Remember("unit", unit);
            var repaired = RealTimeEncounterParticipants<Unit>.For(encounter);
            repaired.Remember("unit", unit);
            Assert.That(repaired.Snapshot().Count, Is.EqualTo(1));
        }

        [Test]
        public void SnapshotCollectionCannotRemoveRetainedParticipants()
        {
            var participants = new RealTimeEncounterParticipants<Unit>();
            participants.Remember("unit", new Unit());
            participants.Snapshot().Clear();
            Assert.That(participants.Snapshot().Count, Is.EqualTo(1));
        }

        [Test]
        public void ConflictingEntityWithSameIdIsRejectedWithoutReplacingOriginal()
        {
            var participants = new RealTimeEncounterParticipants<Unit>();
            var original = new Unit();
            participants.Remember("unit", original);
            Assert.Throws<InvalidOperationException>(() => participants.Remember("unit", new Unit()));
            Assert.That(participants.Snapshot()["unit"], Is.SameAs(original));
        }

        [Test]
        public void MissingEncounterHasNoGlobalFallbackRegistry()
        {
            Assert.That(RealTimeEncounterParticipants<Unit>.For(null), Is.Null);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void InvalidIdIsRejected(string id)
        {
            var participants = new RealTimeEncounterParticipants<Unit>();
            Assert.Throws<InvalidOperationException>(() => participants.Remember(id, new Unit()));
            Assert.That(participants.Snapshot(), Is.Empty);
        }

        [Test]
        public void UnboundedEncounterIsRejectedRatherThanSilentlyDiscardingDeaths()
        {
            var participants = new RealTimeEncounterParticipants<Unit>();
            for (var i = 0; i < 4096; i++) participants.Remember(i.ToString(), new Unit());
            Assert.Throws<InvalidOperationException>(() => participants.Remember("overflow", new Unit()));
            Assert.That(participants.Snapshot().Count, Is.EqualTo(4096));
        }
    }
}
