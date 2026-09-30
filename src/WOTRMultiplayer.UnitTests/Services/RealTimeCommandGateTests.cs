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

            var result = gate.Validate(command, new Dictionary<string, long> { ["unit-a"] = 42 });

            Assert.That(result, Is.EqualTo(RealTimeCommandRejection.None));
        }

        [Test]
        public void Command_ForAnotherPlayersUnit_IsRejected()
        {
            var state = RealTimeState();
            var gate = new RealTimeCommandGate(state);

            var result = gate.Validate(Command(state.Epoch, 1, 42, "unit-a"), new Dictionary<string, long> { ["unit-a"] = 99 });

            Assert.That(result, Is.EqualTo(RealTimeCommandRejection.UnitOwnedByAnotherPlayer));
        }

        [Test]
        public void DuplicateOrReorderedCommand_IsRejected()
        {
            var state = RealTimeState();
            var gate = new RealTimeCommandGate(state);
            var owners = new Dictionary<string, long> { ["unit-a"] = 42 };
            gate.Validate(Command(state.Epoch, 2, 42, "unit-a"), owners);

            var result = gate.Validate(Command(state.Epoch, 1, 42, "unit-a"), owners);

            Assert.That(result, Is.EqualTo(RealTimeCommandRejection.InvalidSequence));
        }

        [Test]
        public void Command_IsRejectedWhileRepairBarrierIsActive()
        {
            var state = RealTimeState();
            state.Phase = CombatSynchronizationPhase.Repairing;
            var gate = new RealTimeCommandGate(state);

            var result = gate.Validate(Command(state.Epoch, 1, 42, "unit-a"), new Dictionary<string, long> { ["unit-a"] = 42 });

            Assert.That(result, Is.EqualTo(RealTimeCommandRejection.SynchronizationBarrierActive));
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
