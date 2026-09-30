using System;
using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeCombatCoordinatorTests
    {
        [Test]
        public void ModeSwitch_WaitsForEveryPlayerBeforeCommit()
        {
            var coordinator = Create();
            coordinator.RequestModeSwitch(CombatMode.RealTimeWithPause, new long[] { -1, 42 });

            Assert.That(coordinator.State.BlocksSaving, Is.True);
            Assert.That(coordinator.AcknowledgeBarrier(-1, new long[] { -1, 42 }), Is.False);
            Assert.That(coordinator.AcknowledgeBarrier(42, new long[] { -1, 42 }), Is.True);

            coordinator.CommitModeSwitch(123UL);
            Assert.That(coordinator.State.Mode, Is.EqualTo(CombatMode.RealTimeWithPause));
            Assert.That(coordinator.State.CanonicalStateHash, Is.EqualTo(123UL));
            Assert.That(coordinator.State.BlocksSaving, Is.False);
        }

        [Test]
        public void StaleCommand_FromPreviousEpoch_IsRejected()
        {
            var coordinator = Create();
            coordinator.RequestModeSwitch(CombatMode.RealTimeWithPause, new long[] { -1 });
            coordinator.AcknowledgeBarrier(-1, new long[] { -1 });
            coordinator.CommitModeSwitch(1UL);
            var oldEpoch = coordinator.State.Epoch;
            coordinator.NextAuthoritativeSequence();
            coordinator.BeginRepair();

            Assert.That(coordinator.IsCurrent(oldEpoch, 2), Is.False);
            Assert.That(coordinator.IsCurrent(coordinator.State.Epoch, 1), Is.True);
        }

        [Test]
        public void Repair_MatchingHash_ResumesRealTime()
        {
            var coordinator = CreateRealTime(10UL);
            Assert.That(coordinator.RequiresRepair(11UL), Is.True);
            coordinator.BeginRepair();

            Assert.That(coordinator.CompleteRepair(20UL, 20UL), Is.True);
            Assert.That(coordinator.State.Mode, Is.EqualTo(CombatMode.RealTimeWithPause));
            Assert.That(coordinator.State.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
        }

        [Test]
        public void Repair_TwoFailures_RequiresTurnBasedFallback()
        {
            var coordinator = CreateRealTime(10UL);
            coordinator.BeginRepair();
            Assert.That(coordinator.CompleteRepair(20UL, 21UL), Is.False);
            coordinator.BeginRepair();
            Assert.That(coordinator.CompleteRepair(30UL, 31UL), Is.False);

            Assert.That(coordinator.State.Phase, Is.EqualTo(CombatSynchronizationPhase.FallingBackToTurnBased));
            coordinator.CommitFallback(30UL);
            Assert.That(coordinator.State.Mode, Is.EqualTo(CombatMode.TurnBased));
        }

        [TestCase(PausePolicy.HostOnly, -1, true)]
        [TestCase(PausePolicy.HostOnly, 42, false)]
        [TestCase(PausePolicy.AnyoneConsensusResume, 42, true)]
        [TestCase(PausePolicy.AnyoneFreeResume, 42, true)]
        public void PausePolicy_ControlsWhoMayPause(PausePolicy policy, long playerId, bool expected)
        {
            var coordinator = Create();
            coordinator.State.PausePolicy = policy;
            Assert.That(coordinator.CanPlayerPause(playerId, -1), Is.EqualTo(expected));
        }

        [Test]
        public void ConsensusResume_RequiresOtherPlayersToReleasePause()
        {
            var coordinator = Create();
            coordinator.State.PausePolicy = PausePolicy.AnyoneConsensusResume;

            Assert.That(coordinator.CanPlayerResume(42, -1, new long[] { 42, 99 }), Is.False);
            Assert.That(coordinator.CanPlayerResume(42, -1, new long[] { 42 }), Is.True);
        }

        [Test]
        public void ConcurrentBarrier_IsRejected()
        {
            var coordinator = Create();
            coordinator.RequestModeSwitch(CombatMode.RealTimeWithPause, new long[] { -1 });
            Assert.Throws<InvalidOperationException>(() => coordinator.BeginRepair());
        }

        private static RealTimeCombatCoordinator Create()
        {
            return new RealTimeCombatCoordinator(new NetworkRealTimeCombatState());
        }

        private static RealTimeCombatCoordinator CreateRealTime(ulong stateHash)
        {
            var coordinator = Create();
            coordinator.RequestModeSwitch(CombatMode.RealTimeWithPause, new long[] { -1 });
            coordinator.AcknowledgeBarrier(-1, new long[] { -1 });
            coordinator.CommitModeSwitch(stateHash);
            return coordinator;
        }
    }
}
