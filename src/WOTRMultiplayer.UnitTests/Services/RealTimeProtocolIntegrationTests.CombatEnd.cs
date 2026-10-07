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
using WOTRMultiplayer.UnitTests.FakeRules;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class RealTimeProtocolIntegrationTests
    {
        [TestCase("session")]
        [TestCase("save")]
        [TestCase("epoch")]
        [TestCase("sender")]
        [TestCase("nonce")]
        public void StaleOrForgedEndPrepareCannotMutateTheClient(string invalid)
        {
            var (host, client, _, clientNetwork, queued, clientCombat) = CreatePair();
            host.Game.RealTimeCombat.Mode = client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            host.CombatEnded();
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(host.Game.Combat, Is.Null); return; }
            host.PublishRealTimeCombatSnapshot();
            var frame = (NotifyRealTimeCombatEnd)queued.Dequeue();
            var encounter = client.Game.Combat;
            var epoch = client.Game.RealTimeCombat.Epoch;
            switch (invalid)
            {
                case "session": frame.SessionId = "another-session"; break;
                case "save": frame.LoadedSaveSeed++; break;
                case "epoch": frame.Epoch = epoch; break;
                case "nonce": frame.EndId = "invalid"; break;
            }
            Fake.ClearRecordedCalls(clientCombat);
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatEnd>(clientNetwork)
                (invalid == "sender" ? 42 : NetworkConstants.HostPlayerId, frame);
            Assert.That(client.Game.Combat, Is.SameAs(encounter));
            Assert.That(client.Game.RealTimeCombat.Epoch, Is.EqualTo(epoch));
            Assert.That(client.Game.RealTimeCombat.CombatEndPending, Is.False);
            Assert.That(queued, Is.Empty);
            A.CallTo(() => clientCombat.TryApplyAndReadRealTimeCombatStateAsync(
                A<NetworkCombatState>._, A<Func<bool>>._)).MustNotHaveHappened();
        }

        [Test]
        public void ClientLocalEndDoesNotFreezeHostReplicationOrReplaceTheEncounterOnRestart()
        {
            var (host, client, _, _, queued, _) = CreatePair();
            host.Game.RealTimeCombat.Mode = client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            var encounter = client.Game.Combat;
            client.CombatEnded();
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(client.Game.Combat, Is.Null); return; }
            Assert.That(client.Game.Combat, Is.SameAs(encounter));
            Assert.That(client.Game.RealTimeCombat.CombatEndPending, Is.False);
            Assert.That(client.IsRealTimeCombatBarrierActive, Is.False);
            Assert.That(client.IsSavingBlockedByCombatSync, Is.True);
            client.CombatStarted();
            Assert.That(client.Game.Combat, Is.SameAs(encounter));
            Assert.That(queued, Is.Empty);
        }

        [Test]
        public void FinalDeathIsCapturedAfterCallbackAndClosedOnlyAfterFinalAck()
        {
            var finalState = new NetworkCombatState();
            var mainThread = new Queue<Action>();
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair(finalState, mainThread);
            host.Game.RealTimeCombat.Mode = client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            host.CombatEnded();
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(host.Game.Combat, Is.Null); return; }
            Assert.That(host.Game.Combat, Is.Not.Null);
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
            Assert.That(queued, Is.Empty);
            host.PublishRealTimeCombatSnapshot();
            Assert.That(queued, Is.Empty, "Capture must wait until the native callback has returned.");
            // Model the final native life/membership writes after CombatEnded.
            finalState.Units.Add(new()
            {
                Id = "last-enemy", Descriptor = new()
                {
                    Damage = 40, Vitals = new() { Damage = 40, LifeState = 2, IsFinallyDead = true, IsInCombat = false }
                }
            });
            mainThread.Dequeue()();
            var prepare = (NotifyRealTimeCombatEnd)queued.Dequeue();
            Assert.That(prepare.State.Units[0].Descriptor.Vitals.IsFinallyDead, Is.True);
            Assert.That(prepare.State.Units[0].Descriptor.Damage, Is.EqualTo(40));
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatEnd>(clientNetwork)(-1, prepare);
            Assert.That(client.IsSavingBlockedByCombatSync, Is.True);
            var prepareAck = (ClientRealTimeCombatEndAck)queued.Dequeue();
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeCombatEndAck>(hostNetwork)(42, prepareAck);
            Assert.That(host.Game.Combat, Is.Not.Null);
            mainThread.Dequeue()();
            var commit = (NotifyRealTimeCombatEnd)queued.Dequeue();
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatEnd>(clientNetwork)(-1, commit);
            Assert.That(client.Game.Combat, Is.Not.Null);
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeCombatEndAck>(hostNetwork)(42,
                (ClientRealTimeCombatEndAck)queued.Dequeue());
            mainThread.Dequeue()();
            var finalize = (NotifyRealTimeCombatEnd)queued.Dequeue();
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatEnd>(clientNetwork)(-1, finalize);
            Assert.That(client.Game.Combat, Is.Null);
            Assert.That(client.IsSavingBlockedByCombatSync, Is.False);
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
            // A lost final acknowledgement is recovered by replaying Finalize,
            // without executing the client's cleanup a second time.
            queued.Dequeue();
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatEnd>(clientNetwork)(-1, finalize);
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeCombatEndAck>(hostNetwork)(42,
                (ClientRealTimeCombatEndAck)queued.Dequeue());
            mainThread.Dequeue()();
            Assert.That(host.Game.Combat, Is.Null);
            Assert.That(host.IsSavingBlockedByCombatSync, Is.False);
        }

        [TestCase("apply")]
        [TestCase("hash")]
        [TestCase("peer")]
        [TestCase("save")]
        [TestCase("epoch")]
        public void FinalCheckpointFailuresNeverReleaseTheHost(string failure)
        {
            var (host, client, hostNetwork, clientNetwork, queued, clientCombat) = CreatePair();
            host.Game.RealTimeCombat.Mode = client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            host.CombatEnded();
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(host.Game.Combat, Is.Null); return; }
            host.PublishRealTimeCombatSnapshot();
            var frame = (NotifyRealTimeCombatEnd)queued.Dequeue();
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatEnd>(clientNetwork)(-1, frame);
            var ack = (ClientRealTimeCombatEndAck)queued.Dequeue();
            switch (failure)
            {
                case "apply": ack.Applied = false; break;
                case "hash": ack.CanonicalStateHash++; break;
                case "save": ack.LoadedSaveSeed++; break;
                case "epoch": ack.Epoch--; break;
            }
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeCombatEndAck>(hostNetwork)(failure == "peer" ? 99 : 42, ack);
            Assert.That(host.Game.Combat, Is.Not.Null);
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
            Assert.That(queued, Is.Empty);
            Assert.That(host.RequestRealTimeCombatModeAsync(CombatMode.TurnBased).GetAwaiter().GetResult(), Is.False);
        }

        [Test]
        public void FailedApplicationMayRetryTheSamePrepareInTheSameEpoch()
        {
            var (host, client, hostNetwork, clientNetwork, queued, clientCombat) = CreatePair();
            host.Game.RealTimeCombat.Mode = client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            host.CombatEnded();
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(host.Game.Combat, Is.Null); return; }
            host.PublishRealTimeCombatSnapshot();
            var frame = (NotifyRealTimeCombatEnd)queued.Dequeue();
            var fail = true;
            A.CallTo(() => clientCombat.TryApplyAndReadRealTimeCombatStateAsync(A<NetworkCombatState>._, A<Func<bool>>._))
                .ReturnsLazily(() => fail ? Task.FromException<NetworkCombatState>(new InvalidOperationException("injected"))
                    : Task.FromResult(new NetworkCombatState()));
            var receive = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatEnd>(clientNetwork);
            receive(-1, frame);
            Assert.That(((ClientRealTimeCombatEndAck)queued.Dequeue()).Applied, Is.False);
            Assert.That(client.IsSavingBlockedByCombatSync, Is.True);
            fail = false;
            receive(-1, frame);
            var successful = (ClientRealTimeCombatEndAck)queued.Dequeue();
            Assert.That(successful.Applied, Is.True);
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeCombatEndAck>(hostNetwork)(42, successful);
            Pump(queued, hostNetwork, clientNetwork);
            Assert.That(host.Game.Combat, Is.Null);
            Assert.That(client.Game.Combat, Is.Null);
        }

        [Test]
        public void LateAckAfterTimeoutStillRecoversWithoutSavingAnUnverifiedState()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            host.Game.RealTimeCombat.Mode = client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            host.CombatEnded();
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(host.Game.Combat, Is.Null); return; }
            host.PublishRealTimeCombatSnapshot();
            var frame = (NotifyRealTimeCombatEnd)queued.Dequeue();
            typeof(MultiplayerHost).GetField("_endDeadline", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(host, DateTime.UtcNow.AddSeconds(-1));
            host.PublishRealTimeCombatSnapshot();
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatEnd>(clientNetwork)(-1, frame);
            Pump(queued, hostNetwork, clientNetwork);
            Assert.That(host.Game.Combat, Is.Null);
            Assert.That(client.Game.Combat, Is.Null);
        }

        [Test]
        public async Task EndDuringModeSwitchWaitsForBothBarriersWithoutDroppingTheEncounter()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            host.Game.RealTimeCombat.Mode = client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.TurnBased), Is.True);
            host.CombatEnded(); client.CombatEnded();
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(host.Game.Combat, Is.Null); return; }
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
            Assert.That(client.IsSavingBlockedByCombatSync, Is.True);
            Pump(queued, hostNetwork, clientNetwork);
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(host.Game.Combat, Is.Not.Null);
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
            Assert.That(client.IsSavingBlockedByCombatSync, Is.True);
            host.PublishRealTimeCombatSnapshot();
            Pump(queued, hostNetwork, clientNetwork);
            Assert.That(host.Game.Combat, Is.Null);
            Assert.That(client.Game.Combat, Is.Null);
        }
    }
}
