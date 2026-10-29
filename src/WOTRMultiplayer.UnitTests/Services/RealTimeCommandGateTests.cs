using System.Collections.Generic;
using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeCommandGateTests
    {
        [Test]
        public void OwnerCommand_WithCurrentEpochAndSequence_IsAccepted()
        {
            var state = RealTimeState();
            var gate = new RealTimeCommandGate(state);
            var command = Command(state.Epoch, 1, 42, "unit-a");

            var result = gate.Validate(command, 42, new Dictionary<string, long> { ["unit-a"] = 42 });

            Assert.That(result, Is.EqualTo(RealTimeCommandRejection.None));
        }

        [Test]
        public void Command_ForAnotherPlayersUnit_IsRejected()
        {
            var state = RealTimeState();
            var gate = new RealTimeCommandGate(state);

            var result = gate.Validate(Command(state.Epoch, 1, 42, "unit-a"), 42, new Dictionary<string, long> { ["unit-a"] = 99 });

            Assert.That(result, Is.EqualTo(RealTimeCommandRejection.UnitOwnedByAnotherPlayer));
        }

        [Test]
        public void DuplicateOrReorderedCommand_IsRejected()
        {
            var state = RealTimeState();
            var gate = new RealTimeCommandGate(state);
            var owners = new Dictionary<string, long> { ["unit-a"] = 42 };
            var accepted = Command(state.Epoch, 2, 42, "unit-a");
            Assert.That(gate.Validate(accepted, 42, owners), Is.EqualTo(RealTimeCommandRejection.None));
            Assert.That(gate.TryCommit(accepted), Is.True);

            var result = gate.Validate(Command(state.Epoch, 1, 42, "unit-a"), 42, owners);

            Assert.That(result, Is.EqualTo(RealTimeCommandRejection.InvalidSequence));
        }

        [Test]
        public void Command_IsRejectedWhileRepairBarrierIsActive()
        {
            var state = RealTimeState();
            state.Phase = CombatSynchronizationPhase.Repairing;
            var gate = new RealTimeCommandGate(state);

            var result = gate.Validate(Command(state.Epoch, 1, 42, "unit-a"), 42, new Dictionary<string, long> { ["unit-a"] = 42 });

            Assert.That(result, Is.EqualTo(RealTimeCommandRejection.SynchronizationBarrierActive));
        }

        [Test]
        public void FirstSequence_InNewEpoch_IsAcceptedWithoutManualReset()
        {
            var state = RealTimeState();
            var gate = new RealTimeCommandGate(state);
            var owners = new Dictionary<string, long> { ["unit-a"] = 42 };
            var accepted = Command(state.Epoch, 9, 42, "unit-a");
            Assert.That(gate.Validate(accepted, 42, owners), Is.EqualTo(RealTimeCommandRejection.None));
            Assert.That(gate.TryCommit(accepted), Is.True);
            state.Epoch++;

            Assert.That(gate.Validate(Command(state.Epoch, 1, 42, "unit-a"), 42, owners), Is.EqualTo(RealTimeCommandRejection.None));
            Assert.That(gate.Validate(Command(state.Epoch - 1, 10, 42, "unit-a"), 42, owners), Is.EqualTo(RealTimeCommandRejection.WrongEpoch));
        }

        [Test]
        public void NonPositiveSequence_IsRejected()
        {
            var state = RealTimeState();
            var gate = new RealTimeCommandGate(state);

            Assert.That(gate.Validate(Command(state.Epoch, 0, 42, "unit-a"), 42, new Dictionary<string, long> { ["unit-a"] = 42 }), Is.EqualTo(RealTimeCommandRejection.InvalidSequence));
        }

        [Test]
        public void SpoofedPlayerId_IsRejectedWithoutConsumingSequence()
        {
            var state = RealTimeState();
            var gate = new RealTimeCommandGate(state);
            var owners = new Dictionary<string, long> { ["unit-a"] = 42 };
            var spoofed = Command(state.Epoch, 1, 42, "unit-a");

            Assert.That(gate.Validate(spoofed, 99, owners), Is.EqualTo(RealTimeCommandRejection.SenderMismatch));
            Assert.That(gate.Validate(spoofed, 42, owners), Is.EqualTo(RealTimeCommandRejection.None));
        }

        [Test]
        public void ExecutionFailure_DoesNotConsumeSequence()
        {
            var state = RealTimeState();
            var gate = new RealTimeCommandGate(state);
            var owners = new Dictionary<string, long> { ["unit-a"] = 42 };
            var command = Command(state.Epoch, 3, 42, "unit-a");

            Assert.That(gate.Validate(command, 42, owners), Is.EqualTo(RealTimeCommandRejection.None));
            // The game rejects execution, so the host never calls Commit.
            Assert.That(gate.Validate(command, 42, owners), Is.EqualTo(RealTimeCommandRejection.None));
            Assert.That(gate.TryCommit(command), Is.True);
            Assert.That(gate.Validate(command, 42, owners), Is.EqualTo(RealTimeCommandRejection.InvalidSequence));
            Assert.That(gate.TryCommit(command), Is.False);
        }

        [Test]
        public void OldEpochCommand_CannotBeCommitted()
        {
            var state = RealTimeState();
            var gate = new RealTimeCommandGate(state);
            var command = Command(state.Epoch, 1, 42, "unit-a");
            state.Epoch++;

            Assert.That(gate.TryCommit(command), Is.False);
        }

        [TestCase("other", 9, RealTimeCommandRejection.WrongSession)]
        [TestCase("session", 8, RealTimeCommandRejection.WrongLoadedSave)]
        public void Command_FromAnotherSessionOrSave_IsRejected(string sessionId, int saveSeed,
            RealTimeCommandRejection expected)
        {
            var state = RealTimeState();
            var gate = new RealTimeCommandGate(state, "session", 9);
            var command = Command(state.Epoch, 1, 42, "unit-a");
            command.SessionId = sessionId;
            command.LoadedSaveSeed = saveSeed;
            command.CommandId = "42:7:1";
            Assert.That(gate.Validate(command, 42,
                new Dictionary<string, long> { ["unit-a"] = 42 }), Is.EqualTo(expected));
        }

        [Test]
        public void Command_WithForgedId_IsRejected()
        {
            var state = RealTimeState();
            var gate = new RealTimeCommandGate(state, "session", 9);
            var command = Command(state.Epoch, 1, 42, "unit-a");
            command.SessionId = "session";
            command.LoadedSaveSeed = 9;
            command.CommandId = "forged";
            Assert.That(gate.Validate(command, 42,
                new Dictionary<string, long> { ["unit-a"] = 42 }),
                Is.EqualTo(RealTimeCommandRejection.InvalidCommandId));
        }

        private static NetworkRealTimeCombatState RealTimeState()
        {
            return new NetworkRealTimeCombatState
            {
                Mode = CombatMode.RealTimeWithPause,
                Epoch = 7,
                Phase = CombatSynchronizationPhase.Stable,
            };
        }

        private static RealTimeCommandEnvelope Command(long epoch, long sequence, long playerId, string unitId)
        {
            return new RealTimeCommandEnvelope
            {
                Epoch = epoch,
                Sequence = sequence,
                PlayerId = playerId,
                UnitId = unitId,
                Kind = RealTimeCommandKind.Move,
            };
        }
    }
}
