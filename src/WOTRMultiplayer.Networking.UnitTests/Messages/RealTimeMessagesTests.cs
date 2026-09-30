using NUnit.Framework;
using ProtoBuf;
using WOTRMultiplayer.Networking.Messages.Contracts;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Networking.UnitTests.Messages
{
    [TestFixture]
    public class RealTimeMessagesTests
    {
        [Test]
        public void Snapshot_RoundTripsEpochSequenceHashAndCombatState()
        {
            var source = new NotifyRealTimeCombatSnapshot
            {
                Epoch = 7,
                Sequence = 12,
                CanonicalStateHash = 987654321UL,
                State = new NetworkCombatState { Units = [new NetworkUnit { Id = "unit-a" }] }
            };

            var copy = Serializer.DeepClone(source);

            Assert.That(copy.Epoch, Is.EqualTo(7));
            Assert.That(copy.Sequence, Is.EqualTo(12));
            Assert.That(copy.CanonicalStateHash, Is.EqualTo(987654321UL));
            Assert.That(copy.State.Units[0].Id, Is.EqualTo("unit-a"));
        }

        [Test]
        public void StateHashReply_RoundTrips()
        {
            var copy = Serializer.DeepClone(new ClientRealTimeStateHash { Epoch = 7, Sequence = 12, CanonicalStateHash = 123UL });

            Assert.That(copy.Epoch, Is.EqualTo(7));
            Assert.That(copy.Sequence, Is.EqualTo(12));
            Assert.That(copy.CanonicalStateHash, Is.EqualTo(123UL));
        }

        [Test]
        public void ClientCommand_RoundTripsMovePayload()
        {
            var source = new ClientRealTimeCombatCommand
            {
                Epoch = 4,
                Sequence = 8,
                PlayerId = 42,
                Kind = 0,
                Movement = new NetworkUnitMoveTo
                {
                    InitiatorUnitId = "unit-a",
                    Destination = new NetworkVector3(1, 2, 3)
                }
            };

            var copy = Serializer.DeepClone(source);

            Assert.That(copy.Epoch, Is.EqualTo(4));
            Assert.That(copy.Sequence, Is.EqualTo(8));
            Assert.That(copy.PlayerId, Is.EqualTo(42));
            Assert.That(copy.Movement.InitiatorUnitId, Is.EqualTo("unit-a"));
            Assert.That(copy.Movement.Destination.Z, Is.EqualTo(3));
        }

        [Test]
        public void AcceptedCommand_RoundTripsAuthoritativeSequence()
        {
            var source = new NotifyRealTimeCombatCommandAccepted
            {
                Epoch = 4,
                AuthoritativeSequence = 12,
                PlayerId = 42,
                ClientSequence = 8,
                Kind = 2,
                Attack = new NetworkUnitAttack { InitiatorUnitId = "unit-a", TargetUnitId = "unit-b" }
            };

            var copy = Serializer.DeepClone(source);

            Assert.That(copy.AuthoritativeSequence, Is.EqualTo(12));
            Assert.That(copy.ClientSequence, Is.EqualTo(8));
            Assert.That(copy.Attack.TargetUnitId, Is.EqualTo("unit-b"));
        }
    }
}
