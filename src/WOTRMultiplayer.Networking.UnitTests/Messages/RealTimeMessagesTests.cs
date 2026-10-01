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
                ResponseTimeMs = 42,
                MissingReplies = 3,
                State = new NetworkCombatState
                {
                    Units = [new NetworkUnit { Id = "unit-a", BlueprintId = "unit-blueprint" }],
                    AreaEffects = [new NetworkAreaEffect
                    {
                        Id = "area-a", BlueprintId = "area-blueprint", CasterUnitId = "caster",
                        OwnerUnitId = "owner", SourceAbilityBlueprintId = "source-ability"
                    }]
                }
            };

            var copy = Serializer.DeepClone(source);

            Assert.That(copy.Epoch, Is.EqualTo(7));
            Assert.That(copy.Sequence, Is.EqualTo(12));
            Assert.That(copy.CanonicalStateHash, Is.EqualTo(987654321UL));
            Assert.That(copy.ResponseTimeMs, Is.EqualTo(42));
            Assert.That(copy.MissingReplies, Is.EqualTo(3));
            Assert.That(copy.State.Units[0].Id, Is.EqualTo("unit-a"));
            Assert.That(copy.State.Units[0].BlueprintId, Is.EqualTo("unit-blueprint"));
            Assert.That(copy.State.AreaEffects[0].BlueprintId, Is.EqualTo("area-blueprint"));
            Assert.That(copy.State.AreaEffects[0].CasterUnitId, Is.EqualTo("caster"));
            Assert.That(copy.State.AreaEffects[0].OwnerUnitId, Is.EqualTo("owner"));
            Assert.That(copy.State.AreaEffects[0].SourceAbilityBlueprintId, Is.EqualTo("source-ability"));
        }

        [Test]
        public void StateHashReply_RoundTrips()
        {
            var copy = Serializer.DeepClone(new ClientRealTimeStateHash { Epoch = 7, Sequence = 12, CanonicalStateHash = 123UL, Applied = true });

            Assert.That(copy.Epoch, Is.EqualTo(7));
            Assert.That(copy.Sequence, Is.EqualTo(12));
            Assert.That(copy.CanonicalStateHash, Is.EqualTo(123UL));
            Assert.That(copy.Applied, Is.True);
        }

        [Test]
        public void StateHashReply_ReportsApplicationFailure()
        {
            var copy = Serializer.DeepClone(new ClientRealTimeStateHash
            {
                Epoch = 7, Sequence = 12, Applied = false, FailureReason = "InvalidOperationException"
            });

            Assert.That(copy.Applied, Is.False);
            Assert.That(copy.FailureReason, Is.EqualTo("InvalidOperationException"));
        }

        [Test]
        public void CombatPreference_RoundTrips()
        {
            var copy = Serializer.DeepClone(new NotifyRealTimeCombatPreference
            {
                Sequence = 3, PreferredMode = 1, PausePolicy = 2
            });

            Assert.That(copy.Sequence, Is.EqualTo(3));
            Assert.That(copy.PreferredMode, Is.EqualTo(1));
            Assert.That(copy.PausePolicy, Is.EqualTo(2));
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

        [Test]
        public void PauseState_RoundTripsVotes()
        {
            var source = new NotifyRealTimePauseChanged
            {
                Epoch = 2,
                Sequence = 4,
                IsPaused = true,
                Policy = 1,
                PausedPlayers = [42],
                ResumeVotes = [-1]
            };

            var copy = Serializer.DeepClone(source);

            Assert.That(copy.Epoch, Is.EqualTo(2));
            Assert.That(copy.Sequence, Is.EqualTo(4));
            Assert.That(copy.PausedPlayers, Is.EquivalentTo(new long[] { 42 }));
            Assert.That(copy.ResumeVotes, Is.EquivalentTo(new long[] { -1 }));
        }

        [Test]
        public void ModeTransition_RoundTripsCheckpointAndStage()
        {
            var source = new NotifyRealTimeModeTransition
            {
                Epoch = 8,
                Stage = (int)RealTimeModeTransitionStage.Commit,
                Mode = 1,
                CanonicalStateHash = 123456UL,
                State = new NetworkCombatState { Units = [new NetworkUnit { Id = "unit-a" }] }
            };

            var copy = Serializer.DeepClone(source);

            Assert.That(copy.Epoch, Is.EqualTo(8));
            Assert.That(copy.Stage, Is.EqualTo((int)RealTimeModeTransitionStage.Commit));
            Assert.That(copy.CanonicalStateHash, Is.EqualTo(123456UL));
            Assert.That(copy.State.Units[0].Id, Is.EqualTo("unit-a"));
        }

        [Test]
        public void ModeTransitionAck_RoundTripsFailureAndHash()
        {
            var copy = Serializer.DeepClone(new ClientRealTimeModeTransitionAck
            {
                Epoch = 8,
                Stage = (int)RealTimeModeTransitionStage.Abort,
                PlayerId = 42,
                Applied = false,
                CanonicalStateHash = 999UL
            });

            Assert.That(copy.PlayerId, Is.EqualTo(42));
            Assert.That(copy.Applied, Is.False);
            Assert.That(copy.CanonicalStateHash, Is.EqualTo(999UL));
        }

        [Test]
        public void RepairMessages_RoundTripStageStateAndAcknowledgement()
        {
            var instruction = Serializer.DeepClone(new NotifyRealTimeRepair
            {
                Epoch = 9,
                Stage = (int)RealTimeRepairStage.Apply,
                Mode = 1,
                CanonicalStateHash = 77UL,
                State = new NetworkCombatState { Units = [new NetworkUnit { Id = "unit-a" }] }
            });
            var ack = Serializer.DeepClone(new ClientRealTimeRepairAck
            {
                Epoch = 9,
                Stage = (int)RealTimeRepairStage.Apply,
                PlayerId = 42,
                CanonicalStateHash = 77UL,
                Applied = true
            });

            Assert.That(instruction.State.Units[0].Id, Is.EqualTo("unit-a"));
            Assert.That(instruction.CanonicalStateHash, Is.EqualTo(77UL));
            Assert.That(ack.PlayerId, Is.EqualTo(42));
            Assert.That(ack.Applied, Is.True);
        }
    }
}
