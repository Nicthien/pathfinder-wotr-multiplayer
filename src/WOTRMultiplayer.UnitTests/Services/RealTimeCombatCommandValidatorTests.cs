using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking.Messages.Contracts;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeCombatCommandValidatorTests
    {
        [Test]
        public void Move_RequiresOneFiniteDestination()
        {
            var command = new ClientRealTimeCombatCommand
            {
                Kind = (int)RealTimeCommandKind.Move,
                Movement = new NetworkUnitMoveTo
                {
                    InitiatorUnitId = "unit-a",
                    Destination = new NetworkVector3(1, 2, 3)
                }
            };

            Assert.That(RealTimeCombatCommandValidator.TryGetUnitId(command, out var unitId), Is.True);
            Assert.That(unitId, Is.EqualTo("unit-a"));
            command.Movement.Destination.X = float.NaN;
            Assert.That(RealTimeCombatCommandValidator.TryGetUnitId(command, out _), Is.False);
        }

        [Test]
        public void MixedPayloads_AreRejected()
        {
            var command = new ClientRealTimeCombatCommand
            {
                Kind = (int)RealTimeCommandKind.Move,
                Movement = new NetworkUnitMoveTo { InitiatorUnitId = "unit-a", Destination = new NetworkVector3() },
                Attack = new NetworkUnitAttack { InitiatorUnitId = "unit-a", TargetUnitId = "unit-b" }
            };

            Assert.That(RealTimeCombatCommandValidator.TryGetUnitId(command, out _), Is.False);
        }

        [Test]
        public void UnsupportedCommand_IsRejected()
        {
            var command = new ClientRealTimeCombatCommand
            {
                Kind = (int)RealTimeCommandKind.Stop,
                Movement = new NetworkUnitMoveTo { InitiatorUnitId = "unit-a", Destination = new NetworkVector3() }
            };

            Assert.That(RealTimeCombatCommandValidator.TryGetUnitId(command, out _), Is.False);
        }
    }
}
