using Newtonsoft.Json;
using NUnit.Framework;
using WOTRMultiplayer.HarmonyPatches.Combat;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.UnitTests
{
    [NonParallelizable]
    public class CombatSaveTurnGuardTests
    {
        [TearDown]
        public void ClearLease() => CombatSaveLoadState.Owner = null;

        [Test]
        public void LoadingCannotStartDelayOrEndATurnEvenThroughDirectCallbacks()
        {
            CombatSaveLoadState.Owner = new object();
            Assert.That(CombatControllerPatches.CombatController_HandleCombatStart_Prefix(), Is.False);
            // Null arguments ensure the lock is checked before any engine access.
            Assert.That(CombatControllerPatches.CombatController_StartTurn_Prefix(null, null), Is.False);
            Assert.That(CombatControllerPatches.CombatController_HandleDelayTurn_Prefix(null, null), Is.False);
            Assert.That(TurnControllerPatches.TurnController_Start_Prefix(null), Is.False);
            Assert.That(TurnControllerPatches.TurnController_Tick_Prefix(null), Is.False);
            var allowed = true;
            Assert.That(TurnControllerPatches.TurnController_CanEndTurn_Prefix(ref allowed), Is.False);
            Assert.That(allowed, Is.False);
            allowed = true;
            Assert.That(TurnControllerPatches.TurnController_CanDelay_Prefix(ref allowed), Is.False);
            Assert.That(allowed, Is.False);
        }

        [Test]
        public void NormalTurnAvailabilityIsNotOverridden()
        {
            var allowed = false;
            Assert.That(TurnControllerPatches.TurnController_CanEndTurn_Prefix(ref allowed), Is.True);
            Assert.That(allowed, Is.False);
            Assert.That(CombatControllerPatches.CombatController_HandleCombatStart_Prefix(), Is.True);
        }

        [Test]
        public void SimulationHoldOnlyFreezesTheBoundWorldAndCanBeReleased()
        {
            var previous = new object(); var loaded = new object();
            CombatSaveLoadState.Owner = new object();
            Assert.That(CombatSaveLoadState.HoldsWorld(previous), Is.False,
                "Preflight must allow the native loader to tick before binding the new world");
            CombatSaveLoadState.BindLoadedWorld(loaded);
            Assert.That(CombatSaveLoadState.HoldsWorld(loaded), Is.True);
            Assert.That(CombatSaveLoadState.HoldsWorld(previous), Is.False);
            CombatSaveLoadState.Owner = null;
            Assert.That(CombatSaveLoadState.HoldsWorld(loaded), Is.False);
        }

        [Test]
        public void ReplacementOperationCannotFreezeThePreviouslyBoundWorld()
        {
            var previous = new object();
            CombatSaveLoadState.Owner = new object();
            CombatSaveLoadState.BindLoadedWorld(previous);
            CombatSaveLoadState.Owner = new object();
            Assert.That(CombatSaveLoadState.HoldsWorld(previous), Is.False);
            Assert.That(CombatSaveLoadState.HoldsWorld(null), Is.False);
        }

        private sealed class PersistedExample
        {
            [JsonProperty] public int ActionsRemaining { get; set; }
            [JsonProperty] public double DistanceSpent { get; set; }
            [JsonProperty] public object CurrentTurn { get; set; }
            public object RuntimeBinding { get; set; }
        }

        [Test]
        public void MemberRebindingPreservesSpentActionsAndRuntimeBindings()
        {
            var binding = new object(); var rebound = new object();
            var source = new PersistedExample { ActionsRemaining = 0, DistanceSpent = 4.5, CurrentTurn = new object() };
            var target = new PersistedExample { ActionsRemaining = 2, RuntimeBinding = binding, CurrentTurn = rebound };
            TurnBasedSaveNativeRestore.CopyPersistedMembers(source, target, "CurrentTurn");
            Assert.That(target.ActionsRemaining, Is.Zero);
            Assert.That(target.DistanceSpent, Is.EqualTo(4.5));
            Assert.That(target.CurrentTurn, Is.SameAs(rebound));
            Assert.That(target.RuntimeBinding, Is.SameAs(binding));
        }
    }
}
