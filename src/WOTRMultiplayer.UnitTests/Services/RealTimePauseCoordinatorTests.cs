using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimePauseCoordinatorTests
    {
        private static readonly long[] Players = [-1, 42];

        [Test]
        public void HostOnly_RejectsClientPauseAndResume()
        {
            var state = Create(PausePolicy.HostOnly);
            var coordinator = new RealTimePauseCoordinator(state);

            Assert.That(coordinator.TryRequest(42, -1, true, Players), Is.False);
            Assert.That(coordinator.TryRequest(-1, -1, true, Players), Is.True);
            Assert.That(coordinator.TryRequest(42, -1, false, Players), Is.False);
            Assert.That(state.IsManualPauseActive, Is.True);
            Assert.That(coordinator.TryRequest(-1, -1, false, Players), Is.True);
            Assert.That(state.IsManualPauseActive, Is.False);
        }

        [Test]
        public void ConsensusResume_WaitsForBothPlayersAndResetsVotesOnNewPause()
        {
            var state = Create(PausePolicy.AnyoneConsensusResume);
            var coordinator = new RealTimePauseCoordinator(state);

            Assert.That(coordinator.TryRequest(42, -1, true, Players), Is.True);
            Assert.That(coordinator.TryRequest(42, -1, false, Players), Is.True);
            Assert.That(state.IsManualPauseActive, Is.True);
            Assert.That(coordinator.TryRequest(-1, -1, true, Players), Is.True);
            Assert.That(state.ResumeVotes, Is.Empty);
            Assert.That(coordinator.TryRequest(-1, -1, false, Players), Is.True);
            Assert.That(state.IsManualPauseActive, Is.True);
            Assert.That(coordinator.TryRequest(42, -1, false, Players), Is.True);
            Assert.That(state.IsManualPauseActive, Is.False);
        }

        [Test]
        public void FreeResume_AllowsOtherPlayerToUnpause()
        {
            var state = Create(PausePolicy.AnyoneFreeResume);
            var coordinator = new RealTimePauseCoordinator(state);

            Assert.That(coordinator.TryRequest(42, -1, true, Players), Is.True);
            Assert.That(coordinator.TryRequest(-1, -1, false, Players), Is.True);
            Assert.That(state.IsManualPauseActive, Is.False);
        }

        [Test]
        public void UnknownPlayerAndRepairBarrier_AreRejected()
        {
            var state = Create(PausePolicy.AnyoneFreeResume);
            var coordinator = new RealTimePauseCoordinator(state);

            Assert.That(coordinator.TryRequest(99, -1, true, Players), Is.False);
            state.Phase = CombatSynchronizationPhase.Repairing;
            Assert.That(coordinator.TryRequest(42, -1, true, Players), Is.False);
        }

        private static NetworkRealTimeCombatState Create(PausePolicy policy)
        {
            return new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, PausePolicy = policy };
        }
    }
}
