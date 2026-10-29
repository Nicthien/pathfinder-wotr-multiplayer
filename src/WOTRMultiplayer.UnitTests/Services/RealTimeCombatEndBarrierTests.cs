using System;
using System.Collections.Generic;
using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeCombatEndBarrierTests
    {
        [Test]
        public void AllPeersMustConfirmEachStageBeforeCompletion()
        {
            var barrier = new RealTimeCombatEndBarrier(new long[] { 42, 43 }, 123);
            for (var stage = 1; stage <= 3; stage++)
            {
                Assert.That(barrier.Acknowledge(42, stage, 123, true), Is.False);
                Assert.That(barrier.Complete, Is.False);
                Assert.That(barrier.Acknowledge(43, stage, 123, true), Is.True);
            }
            Assert.That(barrier.Complete, Is.True);
            Assert.That(barrier.Acknowledge(42, 3, 123, true), Is.False);
        }

        [TestCase(99, 1, 123ul, true)]
        [TestCase(42, 2, 123ul, true)]
        [TestCase(42, 1, 999ul, true)]
        [TestCase(42, 1, 123ul, false)]
        public void UnknownStaleOrRejectedAckNeverAdvances(long peer, int stage, ulong hash, bool applied)
        {
            var barrier = new RealTimeCombatEndBarrier(new long[] { 42 }, 123);
            Assert.That(barrier.Acknowledge(peer, stage, hash, applied), Is.False);
            Assert.That(barrier.Stage, Is.EqualTo(RealTimeCombatEndStage.Prepare));
            Assert.That(barrier.Complete, Is.False);
        }

        [Test]
        public void DuplicatesCannotCountAsTwoPeersAndDisconnectedPeerRemainsRequired()
        {
            var peers = new List<long> { 42, 43 };
            var barrier = new RealTimeCombatEndBarrier(peers, 123);
            peers.Remove(43);
            for (var i = 0; i < 3; i++) Assert.That(barrier.Acknowledge(42, 1, 123, true), Is.False);
            Assert.That(barrier.Stage, Is.EqualTo(RealTimeCombatEndStage.Prepare));
        }

        [Test]
        public void EmptyPeerSetCannotSilentlyValidateFinalCombat()
        {
            Assert.Throws<InvalidOperationException>(() => new RealTimeCombatEndBarrier(Array.Empty<long>(), 123));
        }

        [Test]
        public void EndPendingBlocksSaveOrdersPauseAndModeSwitchEvenWithStablePhase()
        {
            var state = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, CombatEndPending = true };
            Assert.That(state.BlocksSaving, Is.True);
            Assert.That(new RealTimeCommandGate(state).Validate(new RealTimeCommandEnvelope
                { Sequence = 1 }, -1, new Dictionary<string, long>()), Is.EqualTo(RealTimeCommandRejection.SynchronizationBarrierActive));
            Assert.That(new RealTimePauseCoordinator(state).TryRequest(42, -1, false, new long[] { -1, 42 }), Is.False);
            Assert.Throws<InvalidOperationException>(() => new RealTimeCombatCoordinator(state)
                .RequestModeSwitch(CombatMode.TurnBased, new long[] { -1, 42 }));
        }
    }
}
