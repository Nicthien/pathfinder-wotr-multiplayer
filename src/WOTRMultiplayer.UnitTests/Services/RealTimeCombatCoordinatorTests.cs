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
            Assert.That(coordinator.IsCurrent(coordinator.State.Epoch, 1), Is.False);
            Assert.That(coordinator.AcknowledgeBarrier(-1, new long[] { -1, 42 }), Is.False);
            Assert.That(coordinator.AcknowledgeBarrier(42, new long[] { -1, 42 }), Is.True);

            coordinator.CommitModeSwitch(123UL);
            Assert.That(coordinator.State.Mode, Is.EqualTo(CombatMode.RealTimeWithPause));
            Assert.That(coordinator.State.CanonicalStateHash, Is.EqualTo(123UL));
            Assert.That(coordinator.State.BlocksSaving, Is.True);
            Assert.That(coordinator.IsCurrent(coordinator.State.Epoch, 1), Is.False);
            Assert.That(coordinator.AcknowledgeModeCommit(-1, new long[] { -1, 42 }), Is.False);
            Assert.That(coordinator.AcknowledgeModeCommit(42, new long[] { -1, 42 }), Is.True);
            coordinator.CompleteModeCommit();
            Assert.That(coordinator.State.BlocksSaving, Is.False);
            Assert.That(coordinator.IsCurrent(coordinator.State.Epoch, 1), Is.True);
        }

        [Test]
        public void StaleCommand_FromPreviousEpoch_IsRejected()
        {
            var coordinator = Create();
            coordinator.RequestModeSwitch(CombatMode.RealTimeWithPause, new long[] { -1 });
            coordinator.AcknowledgeBarrier(-1, new long[] { -1 });
            coordinator.CommitModeSwitch(1UL);
            coordinator.AcknowledgeModeCommit(-1, new long[] { -1 });
            coordinator.CompleteModeCommit();
            var oldEpoch = coordinator.State.Epoch;
            coordinator.NextAuthoritativeSequence();
            coordinator.BeginRepair();

            Assert.That(coordinator.IsCurrent(oldEpoch, 2), Is.False);
            Assert.That(coordinator.IsCurrent(coordinator.State.Epoch, 1), Is.False,
                "No command or moving snapshot may apply during the repair barrier.");
            Assert.That(coordinator.CompleteRepair(2UL, 2UL), Is.True);
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
        public void SnapshotComparison_UsesHashForAcknowledgedSequence()
        {
            var coordinator = CreateRealTime(1UL);
            var first = coordinator.PublishAuthoritativeSnapshot(10UL);
            var latest = coordinator.PublishAuthoritativeSnapshot(20UL);

            Assert.That(coordinator.TryCompareClientSnapshot(coordinator.State.Epoch, first, 10UL, 10UL, out var repair), Is.True);
            Assert.That(repair, Is.False);
            Assert.That(coordinator.TryCompareClientSnapshot(coordinator.State.Epoch, first, 10UL, 9UL, out repair), Is.True);
            Assert.That(repair, Is.True);
            Assert.That(coordinator.TryCompareClientSnapshot(coordinator.State.Epoch - 1, latest, 20UL, 9UL, out _), Is.False);
            Assert.That(coordinator.TryCompareClientSnapshot(coordinator.State.Epoch, latest + 1, 20UL, 9UL, out _), Is.False);
            Assert.That(coordinator.TryCompareClientSnapshot(coordinator.State.Epoch, latest, 20UL, 20UL, out repair), Is.True);
            Assert.That(repair, Is.False);
            Assert.That(coordinator.TryCompareClientSnapshot(coordinator.State.Epoch, latest, 20UL, 21UL, out repair), Is.True);
            Assert.That(repair, Is.True);
        }

        [Test]
        public void SnapshotPublication_IsBlockedDuringRepair()
        {
            var coordinator = CreateRealTime(1UL);
            coordinator.BeginRepair();

            Assert.Throws<InvalidOperationException>(() => coordinator.PublishAuthoritativeSnapshot(2UL));
        }

        [Test]
        public void Repair_TwoFailures_RequiresTurnBasedFallback()
        {
            var coordinator = CreateRealTime(10UL);
            coordinator.BeginRepair();
            Assert.That(coordinator.CompleteRepair(20UL, 21UL), Is.False);
            Assert.That(coordinator.State.BlocksSaving, Is.True);
            coordinator.BeginRepair();
            Assert.That(coordinator.CompleteRepair(30UL, 31UL), Is.False);

            Assert.That(coordinator.State.Phase, Is.EqualTo(CombatSynchronizationPhase.FallingBackToTurnBased));
            coordinator.CommitFallback(30UL);
            Assert.That(coordinator.State.Mode, Is.EqualTo(CombatMode.TurnBased));
        }

        [Test]
        public void ConcurrentBarrier_IsRejected()
        {
            var coordinator = Create();
            coordinator.RequestModeSwitch(CombatMode.RealTimeWithPause, new long[] { -1 });
            Assert.Throws<InvalidOperationException>(() => coordinator.BeginRepair());
        }

        [Test]
        public void Repair_CannotBeginInTurnBasedMode()
        {
            Assert.Throws<InvalidOperationException>(() => Create().BeginRepair());
        }

        [Test]
        public void UntrustedRuleResult_ForcesTurnBasedFallbackWithoutRetryingRealTime()
        {
            var coordinator = CreateRealTime(10UL);
            coordinator.BeginRepair();
            var epoch = coordinator.State.Epoch;

            coordinator.ForceFallbackAfterUntrustedRuleResult();

            Assert.That(coordinator.State.Epoch, Is.EqualTo(epoch));
            Assert.That(coordinator.State.Phase, Is.EqualTo(CombatSynchronizationPhase.FallingBackToTurnBased));
            Assert.That(coordinator.State.PendingMode, Is.EqualTo(CombatMode.TurnBased));
            Assert.That(coordinator.State.BlocksSaving, Is.True);
        }

        [Test]
        public void ModeSwitch_RequiresAConnectedPlayer()
        {
            Assert.Throws<InvalidOperationException>(() => Create().RequestModeSwitch(CombatMode.RealTimeWithPause, Array.Empty<long>()));
        }

        [Test]
        public void ModeSwitch_CannotCommitUntilEveryOriginalPlayerAcknowledges()
        {
            var coordinator = Create();
            coordinator.RequestModeSwitch(CombatMode.RealTimeWithPause, new long[] { -1, 42 });
            coordinator.AcknowledgeBarrier(-1, new long[] { -1, 42 });

            Assert.Throws<InvalidOperationException>(() => coordinator.CommitModeSwitch(10UL));
            Assert.That(coordinator.AcknowledgeBarrier(42, new long[] { -1 }), Is.False);
            Assert.That(coordinator.State.Mode, Is.EqualTo(CombatMode.TurnBased));
        }

        [Test]
        public void ModeSwitch_CannotResumeUntilCommittedModeIsAppliedByBothPlayers()
        {
            var coordinator = Create();
            coordinator.RequestModeSwitch(CombatMode.RealTimeWithPause, new long[] { -1, 42 });
            coordinator.AcknowledgeBarrier(-1, new long[] { -1, 42 });
            coordinator.AcknowledgeBarrier(42, new long[] { -1, 42 });
            coordinator.CommitModeSwitch(10UL);

            Assert.That(coordinator.AcknowledgeModeCommit(-1, new long[] { -1, 42 }), Is.False);
            Assert.Throws<InvalidOperationException>(() => coordinator.CompleteModeCommit());
            Assert.That(coordinator.State.BlocksSaving, Is.True);
            Assert.That(coordinator.AcknowledgeModeCommit(42, new long[] { -1, 42 }), Is.True);
            coordinator.CompleteModeCommit();
            Assert.That(coordinator.State.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
        }

        [Test]
        public void AbortAfterCommit_RestoresPreviousMode()
        {
            var coordinator = Create();
            coordinator.RequestModeSwitch(CombatMode.RealTimeWithPause, new long[] { -1 });
            coordinator.AcknowledgeBarrier(-1, new long[] { -1 });
            coordinator.CommitModeSwitch(10UL);

            coordinator.AbortModeSwitch();

            Assert.That(coordinator.State.Mode, Is.EqualTo(CombatMode.TurnBased));
            Assert.That(coordinator.State.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
        }

        [Test]
        public void AbortWithoutModeSwitch_IsRejected()
        {
            Assert.Throws<InvalidOperationException>(() => Create().AbortModeSwitch());
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
            coordinator.AcknowledgeModeCommit(-1, new long[] { -1 });
            coordinator.CompleteModeCommit();
            return coordinator;
        }
    }
}
