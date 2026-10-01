using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class TurnEndUnitUpdatePreflightTests
    {
        [Test]
        public void CompleteExistingUnit_IsAccepted()
        {
            Assert.That(TurnEndUnitUpdatePreflight.FindFailure([Sample()], id => id == "hero", true,
                _ => "unit-blueprint", _ => "scene-a"), Is.Null);
        }

        [Test]
        public void MissingUnitOrState_IsRejectedBeforeUpdate()
        {
            Assert.That(TurnEndUnitUpdatePreflight.FindFailure([Sample()], _ => false, true),
                Is.EqualTo("missing-unit:hero"));
            var unit = Sample();
            unit.Descriptor.State.Prone = null;
            Assert.That(TurnEndUnitUpdatePreflight.FindFailure([unit], _ => true, true),
                Is.EqualTo("invalid-state:hero"));
        }

        [Test]
        public void DuplicateUnitAndNonFinitePosition_AreRejected()
        {
            Assert.That(TurnEndUnitUpdatePreflight.FindFailure([Sample(), Sample()], _ => true, true),
                Is.EqualTo("duplicate-unit:hero"));
            var unit = Sample();
            unit.Position.X = float.NaN;
            Assert.That(TurnEndUnitUpdatePreflight.FindFailure([unit], _ => true, true),
                Is.EqualTo("invalid-position:hero"));
        }

        [Test]
        public void WrongBlueprintOrScene_IsRejectedBeforeMutation()
        {
            Assert.That(TurnEndUnitUpdatePreflight.FindFailure([Sample()], _ => true, true,
                _ => "another-blueprint", _ => "scene-a"), Is.EqualTo("wrong-blueprint:hero"));
            Assert.That(TurnEndUnitUpdatePreflight.FindFailure([Sample()], _ => true, true,
                _ => "unit-blueprint", _ => "scene-b"), Is.EqualTo("wrong-scene:hero"));
        }

        private static NetworkUnit Sample() => new()
        {
            Id = "hero",
            BlueprintId = "unit-blueprint",
            SceneName = "scene-a",
            Position = new NetworkVector3(),
            Descriptor = new NetworkUnitDescriptor
            {
                State = new NetworkUnitState { Prone = new NetworkUnitProneState() }
            }
        };
    }
}
