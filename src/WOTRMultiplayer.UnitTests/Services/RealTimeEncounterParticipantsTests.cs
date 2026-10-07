using System;
using NUnit.Framework;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeEncounterParticipantsTests
    {
        private sealed class Unit { public bool InCombat = true; public bool Dead; }

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
