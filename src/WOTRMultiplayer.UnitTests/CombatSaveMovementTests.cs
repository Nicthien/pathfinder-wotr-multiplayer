using NUnit.Framework;
using ProtoBuf;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.UnitTests
{
    public class CombatSaveMovementTests
    {
        private static NotifyTurnMovementState Frame() => new() { Session = "session", SessionSeed = 1,
            LoadSeed = 2, TurnSeed = 3, Unit = "rider", Movement = 4, Sequence = 5, State = "{}" };

        [Test]
        public void CompletedMovementRoundTripsOverNetwork()
        {
            var copy = Serializer.DeepClone(Frame());
            Assert.That(TurnMovementState.IsCurrent(copy, "session", 1, 2, 3, "rider", 4, 0), Is.True);
            Assert.That(copy.State, Is.EqualTo("{}"));
        }

        [Test]
        public void OldMovementCannotUndoANewerCommandOrAnotherTurn()
        {
            var frame = Frame();
            Assert.That(TurnMovementState.IsCurrent(frame, "session", 1, 2, 3, "rider", 5, 0), Is.False);
            Assert.That(TurnMovementState.IsCurrent(frame, "session", 1, 2, 4, "rider", 4, 0), Is.False);
            Assert.That(TurnMovementState.IsCurrent(frame, "session", 1, 3, 3, "rider", 4, 0), Is.False);
            Assert.That(TurnMovementState.IsCurrent(frame, "other", 1, 2, 3, "rider", 4, 0), Is.False);
            Assert.That(TurnMovementState.IsCurrent(frame, "session", 2, 2, 3, "rider", 4, 0), Is.False);
            Assert.That(TurnMovementState.IsCurrent(frame, "session", 1, 2, 3, "other", 4, 0), Is.False);
            Assert.That(TurnMovementState.IsCurrent(frame, "session", 1, 2, 3, "rider", 4, 5), Is.False);
        }
    }
}
