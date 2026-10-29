using System.Linq;
using System;
using System.Threading.Tasks;
using NUnit.Framework;
using ProtoBuf;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking.Messages.Contracts;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeAcceptedOrderLedgerTests
    {
        [Test]
        public void SameAcceptedCommand_ProducesSamePayloadAndIgnoresDuplicate()
        {
            var host = new NetworkRealTimeCombatState { Epoch = 3 };
            var client = new NetworkRealTimeCombatState { Epoch = 3 };
            var accepted = Move(3, 7, 1);

            RealTimeAcceptedOrderLedger.Record(host, accepted);
            var transmitted = Serializer.DeepClone(accepted);
            RealTimeAcceptedOrderLedger.Record(client, transmitted);
            RealTimeAcceptedOrderLedger.Record(client, transmitted);

            Assert.That(RealTimeAcceptedOrderLedger.Snapshot(host).Length, Is.EqualTo(1));
            Assert.That(RealTimeAcceptedOrderLedger.Snapshot(client).Length, Is.EqualTo(1));
            Assert.That(RealTimeAcceptedOrderLedger.Snapshot(host)[0].Payload,
                Is.EqualTo(RealTimeAcceptedOrderLedger.Snapshot(client)[0].Payload));
        }

        [Test]
        public void ChangedDestination_ChangesAcceptedOrderFingerprint()
        {
            var first = new NetworkRealTimeCombatState { Epoch = 3 };
            var second = new NetworkRealTimeCombatState { Epoch = 3 };
            RealTimeAcceptedOrderLedger.Record(first, Move(3, 7, 1));
            RealTimeAcceptedOrderLedger.Record(second, Move(3, 7, 2));

            Assert.That(RealTimeAcceptedOrderLedger.Snapshot(first)[0].Payload,
                Is.Not.EqualTo(RealTimeAcceptedOrderLedger.Snapshot(second)[0].Payload));
        }

        [Test]
        public void NewEpoch_HidesPreviousOrders()
        {
            var state = new NetworkRealTimeCombatState { Epoch = 3 };
            RealTimeAcceptedOrderLedger.Record(state, Move(3, 7, 1));
            state.Epoch = 4;

            Assert.That(RealTimeAcceptedOrderLedger.Snapshot(state), Is.Empty);
            RealTimeAcceptedOrderLedger.Record(state, Move(4, 1, 1));
            Assert.That(RealTimeAcceptedOrderLedger.Snapshot(state).Length, Is.EqualTo(1));
        }

        [Test]
        public void ConcurrentRecordAndSnapshot_RetainLatestOrderedCommands()
        {
            var state = new NetworkRealTimeCombatState { Epoch = 3 };

            Parallel.For(1, 129, sequence =>
            {
                RealTimeAcceptedOrderLedger.Record(state, Move(3, sequence, sequence));
                var current = RealTimeAcceptedOrderLedger.Snapshot(state);
                Assert.That(current.Length, Is.LessThanOrEqualTo(32));
                Assert.That(current.Select(order => order.Sequence), Is.Ordered);
            });

            var retained = RealTimeAcceptedOrderLedger.Snapshot(state);
            Assert.That(retained.Select(order => order.Sequence),
                Is.EqualTo(Enumerable.Range(97, 32).Select(value => (long)value)));
        }

        private static NotifyRealTimeCombatCommandAccepted Move(long epoch, long sequence, float x) => new()
        {
            Epoch = epoch,
            AuthoritativeSequence = sequence,
            PlayerId = 42,
            Kind = (int)RealTimeCommandKind.Move,
            Movement = new NetworkUnitMoveTo
            {
                InitiatorUnitId = "hero",
                Destination = new NetworkVector3(x, 0, 0)
            }
        };

        private static NetworkRealTimeCombatState RepairState() => new()
        {
            Epoch = 3, LastAuthoritativeSequence = 9, Mode = CombatMode.RealTimeWithPause,
            Phase = CombatSynchronizationPhase.Repairing
        };

        private static void RecordBoundOrder(NetworkRealTimeCombatState state)
        {
            var accepted = Move(3, 7, 1);
            accepted.SessionId = "session"; accepted.LoadedSaveSeed = 12; accepted.CommandId = "42:3:1";
            RealTimeAcceptedOrderLedger.Record(state, accepted);
        }

        [Test]
        public void RestoreEvidenceDoesNotExecuteCommandsOrAdvanceTheCursorOrReleaseBarrier()
        {
            var host = RepairState(); RecordBoundOrder(host);
            var reference = RealTimeAcceptedOrderLedger.CaptureReference(host, "session", 12);
            var client = RepairState(); client.LastAuthoritativeSequence = 1;
            RealTimeAcceptedOrderLedger.RestoreReference(client, reference, "session", 12, 3, 9, () => true);
            Assert.That(RealTimeAcceptedOrderLedger.Snapshot(client).Single().Payload,
                Is.EqualTo(RealTimeAcceptedOrderLedger.Snapshot(host).Single().Payload));
            Assert.That(client.LastAuthoritativeSequence, Is.EqualTo(1));
            Assert.That(client.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(client.BlocksSaving, Is.True);
            reference.Orders[0].UnitId = "changed-after-restoration";
            var snapshot = RealTimeAcceptedOrderLedger.Snapshot(client); snapshot[0].UnitId = "changed-snapshot";
            Assert.That(RealTimeAcceptedOrderLedger.Snapshot(client).Single().UnitId, Is.EqualTo("hero"));
        }

        [TestCase("revoked")][TestCase("stable")][TestCase("turn-based")][TestCase("epoch")][TestCase("ahead")]
        [TestCase("session")][TestCase("save")][TestCase("included")]
        public void InvalidBarrierDoesNotReplaceExistingLedger(string fault)
        {
            var host = RepairState(); RecordBoundOrder(host);
            var reference = RealTimeAcceptedOrderLedger.CaptureReference(host, "session", 12);
            var client = RepairState(); RecordBoundOrder(client);
            var existing = client.AcceptedOrders;
            if (fault == "stable") client.Phase = CombatSynchronizationPhase.Stable;
            if (fault == "turn-based") client.Mode = CombatMode.TurnBased;
            if (fault == "epoch") client.Epoch++;
            if (fault == "ahead") client.LastAuthoritativeSequence++;
            Assert.Catch(() => RealTimeAcceptedOrderLedger.RestoreReference(client, reference,
                fault == "session" ? "other" : "session", fault == "save" ? 13 : 12,
                3, fault == "included" ? 10 : 9, () => fault != "revoked"));
            Assert.That(client.AcceptedOrders, Is.SameAs(existing));
        }

        [TestCase("payload")][TestCase("session")][TestCase("sequence")][TestCase("missing")]
        public void HostCannotVerifyAChangedJournal(string fault)
        {
            var host = RepairState(); RecordBoundOrder(host);
            var reference = RealTimeAcceptedOrderLedger.CaptureReference(host, "session", 12);
            RealTimeAcceptedOrderLedger.RequireUnchanged(host, reference, "session", 12);
            if (fault == "payload") host.AcceptedOrders[0].Payload = Convert.ToBase64String(new byte[32]);
            if (fault == "session") host.AcceptedOrders[0].SessionId = "other";
            if (fault == "sequence") host.LastAuthoritativeSequence++;
            if (fault == "missing") host.AcceptedOrders.Clear();
            Assert.Catch(() => RealTimeAcceptedOrderLedger.RequireUnchanged(host, reference, "session", 12));
        }

        [Test]
        public void VerifiedSnapshotCommitsOnlyEvidenceAndItsCursorInStableRealTime()
        {
            var host = RepairState(); RecordBoundOrder(host);
            var reference = RealTimeAcceptedOrderLedger.CaptureReference(host, "session", 12);
            var client = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 3 };
            RealTimeAcceptedOrderLedger.CommitSnapshotReference(client, reference, "session", 12, 3, 9, 123, () => true);
            Assert.That(client.LastAuthoritativeSequence, Is.EqualTo(9));
            Assert.That(client.CanonicalStateHash, Is.EqualTo(123));
            Assert.That(client.AcceptedOrders.Single().CommandId, Is.EqualTo("42:3:1"));
            Assert.That(client.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            reference.Orders[0].UnitId = "mutated";
            Assert.That(client.AcceptedOrders.Single().UnitId, Is.EqualTo("hero"));
        }

        [TestCase("revoked")][TestCase("repair")][TestCase("turn-based")][TestCase("end")]
        [TestCase("epoch")][TestCase("duplicate")][TestCase("ahead")]
        public void SnapshotCommitCannotReleaseABarrierOrRegressItsCursor(string fault)
        {
            var host = RepairState(); RecordBoundOrder(host);
            var reference = RealTimeAcceptedOrderLedger.CaptureReference(host, "session", 12);
            var client = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 3, CanonicalStateHash = 99 };
            if (fault == "repair") client.Phase = CombatSynchronizationPhase.Repairing;
            if (fault == "turn-based") client.Mode = CombatMode.TurnBased;
            if (fault == "end") client.CombatEndPending = true;
            if (fault == "epoch") client.Epoch++;
            if (fault == "duplicate") client.LastAuthoritativeSequence = 9;
            if (fault == "ahead") client.LastAuthoritativeSequence = 10;
            var cursor = client.LastAuthoritativeSequence; var journal = client.AcceptedOrders;
            Assert.Throws<InvalidOperationException>(() => RealTimeAcceptedOrderLedger.CommitSnapshotReference(
                client, reference, "session", 12, 3, 9, 123, () => fault != "revoked"));
            Assert.That(client.LastAuthoritativeSequence, Is.EqualTo(cursor));
            Assert.That(client.AcceptedOrders, Is.SameAs(journal));
            Assert.That(client.CanonicalStateHash, Is.EqualTo(99));
        }
    }
}
