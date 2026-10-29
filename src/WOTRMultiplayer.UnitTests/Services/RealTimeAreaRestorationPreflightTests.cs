using System;
using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeAreaRestorationPreflightTests
    {
        private static NetworkCombatState Checkpoint()
        {
            var state = RealTimeAreaReplicaFactoryTests.Source().State;
            state.SceneName = "room";
            return new NetworkCombatState { AreaEffects = [state] };
        }

        [Test]
        public void ProspectiveAreaNeverQueriesMissingLocalData()
        {
            Assert.That(RealTimeCombatEntityPreflight.FindMissing(Checkpoint(), _ => true, _ => false,
                hasAreaView: _ => throw new Exception("view"),
                getAreaBlueprintId: _ => throw new Exception("blueprint"),
                getAreaSceneName: _ => throw new Exception("scene"),
                getLocalArea: _ => throw new Exception("local"), canRecreateArea: _ => true), Is.Null);
        }

        [TestCase("position")]
        [TestCase("orientation")]
        [TestCase("occupants")]
        [TestCase("context")]
        [TestCase("target")]
        [TestCase("scene")]
        public void ReconstructionCannotBypassStateValidation(string invalid)
        {
            var state = Checkpoint(); var area = state.AreaEffects[0];
            switch (invalid)
            {
                case "position": area.Position.X = float.NaN; break;
                case "orientation": area.Orientation = float.PositiveInfinity; break;
                case "occupants": area.UnitsInside = ["hero", "HERO"]; break;
                case "context": area.ContextChain = null; break;
                case "target": area.TargetUnitId = "hero"; break;
                case "scene": area.SceneName = null; break;
            }
            Assert.That(RealTimeCombatEntityPreflight.FindMissing(state, _ => true, _ => false,
                canRecreateArea: _ => true), Does.Contain("invalid-area:zone:"));
        }

        [Test]
        public void ProspectiveOccupantsStillRequireRealUnits()
        {
            var state = Checkpoint(); state.AreaEffects[0].UnitsInside = ["missing"];
            Assert.That(RealTimeCombatEntityPreflight.FindMissing(state, _ => false, _ => false,
                canRecreateArea: _ => true), Does.Contain("missing-area-unit:zone:missing"));
        }

        [Test]
        public void MissingAreaIsRejectedWithoutExplicitReconstructionPermission() =>
            Assert.That(RealTimeCombatEntityPreflight.FindMissing(Checkpoint(), _ => true, _ => false),
                Is.EqualTo("missing-area:zone"));

        [Test]
        public void OnlyOwnedExtraAreasMayBeRemoved()
        {
            Assert.That(RealTimeCombatEntityPreflight.FindMissing(new NetworkCombatState(), _ => true, _ => true,
                localAreaIds: ["replica", "native"], canRemoveArea: id => id == "replica"),
                Is.EqualTo("extra-area:native"));
        }
    }
}
