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
    }
}
