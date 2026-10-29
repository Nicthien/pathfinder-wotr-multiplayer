using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class RealTimeProtocolIntegrationTests
    {
        private static NotifyRealTimeAuthoritativeEvent SimpleAction() => new()
        {
            UnitId = "hero", ActionId = "action", Kind = (int)RealTimeAuthoritativeEventKind.AttackPresentation,
            Attack = new RealTimeAttackPresentationPayload { Stage = 1, SpeedScale = 1 }
        };

        [TestCase(false)][TestCase(true)]
        public void SnapshotArrivingBeforeActionOrDurableResultWaitsAndNeverStopsTheAnimation(bool durableResult)
        {
            if (!RealTimePresentationRuntime.Enabled) return;
            var (host, client, _, network, queued, combat) = CreatePair();
            host.Game.RealTimeCombat.Mode = host.Game.RealTimeCombat.PreferredMode = CombatMode.RealTimeWithPause;
            client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            var order = new List<string>();
            A.CallTo(() => combat.TryPresentRealTimeActionAsync(A<NotifyRealTimeAuthoritativeEvent>._, A<Func<bool>>._))
                .ReturnsLazily(() => { order.Add("event"); return Task.FromResult<bool?>(true); });
            A.CallTo(() => combat.TryApplyAndReadRealTimeCombatStateAsync(A<NetworkCombatState>._, A<Func<bool>>._))
                .ReturnsLazily(() => { order.Add("snapshot"); return Task.FromResult(new NetworkCombatState()); });
            host.PublishRealTimeAuthoritativeEvent(durableResult ? UnitResult("hero") : SimpleAction());
            var frame = (NotifyRealTimeAuthoritativeEvent)queued.Dequeue();
            host.PublishRealTimeCombatSnapshot(); var snapshot = (NotifyRealTimeCombatSnapshot)queued.Dequeue();
            var receiveSnapshot = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(network);
            receiveSnapshot(NetworkConstants.HostPlayerId, snapshot);
            Assert.That(order, Is.Empty);
            Assert.That(client.Game.RealTimeCombat.LastAuthoritativeSequence, Is.Zero);
            Assert.That(queued, Is.Empty);
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeAuthoritativeEvent>(network)(NetworkConstants.HostPlayerId, frame);
            Assert.That(order, Is.EqualTo(new[] { "event", "snapshot" }));
            Assert.That(client.Game.RealTimeCombat.LastAuthoritativeSequence, Is.EqualTo(snapshot.Sequence));
            Assert.That(queued.Dequeue(), Is.TypeOf<ClientRealTimeStateHash>());
            receiveSnapshot(NetworkConstants.HostPlayerId, snapshot);
            A.CallTo(() => combat.TickRealTimePresentation(false)).MustNotHaveHappened();
            A.CallTo(() => combat.TryExecuteRealTimeCombatCommandAsync(A<RealTimeCommandKind>._,
                A<NetworkUnitMoveTo>._, A<NetworkUnitAttack>._,
                A<NetworkAbilityUse>._, A<Func<bool>>._)).MustNotHaveHappened();
            Assert.That(order.Count, Is.EqualTo(2));
            host.Reset();
        }

        [Test]
        public void SnapshotOlderThanAnAppliedResultCannotRollBackThatResult()
        {
            if (!RealTimePresentationRuntime.Enabled) return;
            var (host, client, _, network, queued, combat) = CreatePair();
            host.Game.RealTimeCombat.Mode = host.Game.RealTimeCombat.PreferredMode = CombatMode.RealTimeWithPause;
            client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            A.CallTo(() => combat.TryPresentRealTimeActionAsync(A<NotifyRealTimeAuthoritativeEvent>._, A<Func<bool>>._))
                .Returns(Task.FromResult<bool?>(true));
            host.PublishRealTimeAuthoritativeEvent(SimpleAction()); var first = (NotifyRealTimeAuthoritativeEvent)queued.Dequeue();
            host.PublishRealTimeCombatSnapshot(); var older = (NotifyRealTimeCombatSnapshot)queued.Dequeue();
            host.PublishRealTimeAuthoritativeEvent(UnitResult("hero")); var second = (NotifyRealTimeAuthoritativeEvent)queued.Dequeue();
            var receiveEvent = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeAuthoritativeEvent>(network);
            receiveEvent(NetworkConstants.HostPlayerId, first); receiveEvent(NetworkConstants.HostPlayerId, second);
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(network)(NetworkConstants.HostPlayerId, older);
            A.CallTo(() => combat.TryApplyAndReadRealTimeCombatStateAsync(A<NetworkCombatState>._, A<Func<bool>>._)).MustNotHaveHappened();
            Assert.That(queued, Is.Empty);
            host.Reset();
        }

        [Test]
        public void FailedEventCannotBeSkippedByTheWaitingSnapshot()
        {
            if (!RealTimePresentationRuntime.Enabled) return;
            var (host, client, _, network, queued, combat) = CreatePair();
            host.Game.RealTimeCombat.Mode = host.Game.RealTimeCombat.PreferredMode = CombatMode.RealTimeWithPause;
            client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            A.CallTo(() => combat.TryPresentRealTimeActionAsync(A<NotifyRealTimeAuthoritativeEvent>._, A<Func<bool>>._))
                .Returns(Task.FromResult<bool?>(false));
            host.PublishRealTimeAuthoritativeEvent(SimpleAction()); var frame = (NotifyRealTimeAuthoritativeEvent)queued.Dequeue();
            host.PublishRealTimeCombatSnapshot(); var snapshot = (NotifyRealTimeCombatSnapshot)queued.Dequeue();
            var receiveSnapshot = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(network);
            receiveSnapshot(NetworkConstants.HostPlayerId, snapshot);
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeAuthoritativeEvent>(network)(NetworkConstants.HostPlayerId, frame);
            receiveSnapshot(NetworkConstants.HostPlayerId, snapshot);
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            A.CallTo(() => combat.TryApplyAndReadRealTimeCombatStateAsync(A<NetworkCombatState>._, A<Func<bool>>._)).MustNotHaveHappened();
            Assert.That(queued.Dequeue(), Is.TypeOf<ClientRealTimeCommandFault>());
            Assert.That(queued, Is.Empty);
            host.Reset();
        }

        [Test]
        public void MissingEventTimesOutEvenIfNoLaterEventEverArrives()
        {
            if (!RealTimePresentationRuntime.Enabled) return;
            var (host, client, _, network, queued, combat) = CreatePair();
            host.Game.RealTimeCombat.Mode = host.Game.RealTimeCombat.PreferredMode = CombatMode.RealTimeWithPause;
            client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            host.PublishRealTimeAuthoritativeEvent(SimpleAction()); queued.Dequeue(); // Simulate the lost event.
            host.PublishRealTimeCombatSnapshot(); var snapshot = (NotifyRealTimeCombatSnapshot)queued.Dequeue();
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(network)(NetworkConstants.HostPlayerId, snapshot);
            var barrier = typeof(MultiplayerClient).GetField("_presentationSnapshotBarrier", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(client);
            typeof(RealTimePresentationSnapshotBarrier).GetField("_waitingSince", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(barrier, (DateTime?)DateTime.UtcNow.AddSeconds(-6));
            client.TickRealTimePresentation();
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(queued.Dequeue(), Is.TypeOf<ClientRealTimeCommandFault>());
            A.CallTo(() => combat.TryApplyAndReadRealTimeCombatStateAsync(A<NetworkCombatState>._, A<Func<bool>>._)).MustNotHaveHappened();
            host.Reset();
        }

        [Test]
        public void NewEpochDoesNotReuseTheOldWaitingSnapshot()
        {
            if (!RealTimePresentationRuntime.Enabled) return;
            var (host, client, _, network, queued, combat) = CreatePair();
            host.Game.RealTimeCombat.Mode = host.Game.RealTimeCombat.PreferredMode = CombatMode.RealTimeWithPause;
            client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            A.CallTo(() => combat.TryPresentRealTimeActionAsync(A<NotifyRealTimeAuthoritativeEvent>._, A<Func<bool>>._))
                .Returns(Task.FromResult<bool?>(true));
            host.PublishRealTimeAuthoritativeEvent(SimpleAction()); var frame = (NotifyRealTimeAuthoritativeEvent)queued.Dequeue();
            host.PublishRealTimeCombatSnapshot(); var snapshot = (NotifyRealTimeCombatSnapshot)queued.Dequeue();
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(network)(NetworkConstants.HostPlayerId, snapshot);
            client.Game.RealTimeCombat.Epoch++;
            frame.Epoch = client.Game.RealTimeCombat.Epoch; frame.EventId = $"{frame.Epoch}:1";
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeAuthoritativeEvent>(network)(NetworkConstants.HostPlayerId, frame);
            A.CallTo(() => combat.TryApplyAndReadRealTimeCombatStateAsync(A<NetworkCombatState>._, A<Func<bool>>._)).MustNotHaveHappened();
            Assert.That(queued, Is.Empty);
            host.Reset();
        }

        [TestCase(CombatSynchronizationPhase.Repairing, false)]
        [TestCase(CombatSynchronizationPhase.PreparingModeSwitch, false)]
        [TestCase(CombatSynchronizationPhase.FallingBackToTurnBased, false)]
        [TestCase(CombatSynchronizationPhase.Repairing, true)]
        [TestCase(CombatSynchronizationPhase.PreparingModeSwitch, true)]
        [TestCase(CombatSynchronizationPhase.FallingBackToTurnBased, true)]
        public void SnapshotQueuedBeforeANewBarrierCannotApplyOrAcknowledge(CombatSynchronizationPhase phase, bool failure)
        {
            var (host, client, _, network, queued, combat) = CreatePair();
            host.Game.RealTimeCombat.Mode = host.Game.RealTimeCombat.PreferredMode = CombatMode.RealTimeWithPause;
            client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            host.PublishRealTimeCombatSnapshot(); var snapshot = (NotifyRealTimeCombatSnapshot)queued.Dequeue();
            A.CallTo(() => combat.TryApplyAndReadRealTimeCombatStateAsync(A<NetworkCombatState>._, A<Func<bool>>._))
                .ReturnsLazily((NetworkCombatState _, Func<bool> authorized) =>
                {
                    client.Game.RealTimeCombat.Phase = phase;
                    Assert.That(authorized(), Is.False);
                    if (failure) return Task.FromException<NetworkCombatState>(new InvalidOperationException("obsolete-dispatch"));
                    return Task.FromResult<NetworkCombatState>(null);
                });
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(network)(NetworkConstants.HostPlayerId, snapshot);
            Assert.That(queued, Is.Empty);
            Assert.That(client.Game.RealTimeCombat.LastAuthoritativeSequence, Is.Zero);
            host.Reset();
        }

        [Test]
        public async Task WaitingSnapshotRequiresSuccessfulEventCompletionNotJustItsArrival()
        {
            if (!RealTimePresentationRuntime.Enabled) return;
            var (host, client, _, network, queued, combat) = CreatePair();
            host.Game.RealTimeCombat.Mode = host.Game.RealTimeCombat.PreferredMode = CombatMode.RealTimeWithPause;
            client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            var eventCompletion = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acknowledgement = new TaskCompletionSource<ClientRealTimeStateHash>(TaskCreationOptions.RunContinuationsAsynchronously);
            A.CallTo(() => combat.TryPresentRealTimeActionAsync(A<NotifyRealTimeAuthoritativeEvent>._, A<Func<bool>>._))
                .Returns(eventCompletion.Task);
            A.CallTo(() => network.Broadcast(A<object>._)).Invokes(call =>
            {
                if (call.Arguments[0] is ClientRealTimeStateHash ack) acknowledgement.TrySetResult(ack);
            });
            host.PublishRealTimeAuthoritativeEvent(SimpleAction()); var frame = (NotifyRealTimeAuthoritativeEvent)queued.Dequeue();
            host.PublishRealTimeCombatSnapshot(); var snapshot = (NotifyRealTimeCombatSnapshot)queued.Dequeue();
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(network)(NetworkConstants.HostPlayerId, snapshot);
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeAuthoritativeEvent>(network)(NetworkConstants.HostPlayerId, frame);
            A.CallTo(() => combat.TryApplyAndReadRealTimeCombatStateAsync(A<NetworkCombatState>._, A<Func<bool>>._)).MustNotHaveHappened();
            Assert.That(acknowledgement.Task.IsCompleted, Is.False);
            eventCompletion.SetResult(true);
            Assert.That(await Task.WhenAny(acknowledgement.Task, Task.Delay(5000)), Is.SameAs(acknowledgement.Task));
            Assert.That((await acknowledgement.Task).Sequence, Is.EqualTo(snapshot.Sequence));
            Assert.That(client.Game.RealTimeCombat.LastAuthoritativeSequence, Is.EqualTo(snapshot.Sequence));
            host.Reset();
        }
    }
}
