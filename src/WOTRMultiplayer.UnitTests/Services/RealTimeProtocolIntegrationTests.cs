using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutoMapper;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Abstractions.Hashing;
using WOTRMultiplayer.Abstractions.IO;
using WOTRMultiplayer.Abstractions.Random;
using WOTRMultiplayer.Abstractions.Settings;
using WOTRMultiplayer.Abstractions.Unity;
using WOTRMultiplayer.Config.Mapping;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Abstractions;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.UnitTests.FakeRules;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeProtocolIntegrationTests
    {
        [Test]
        public async Task TwoActors_LostAndDuplicatePrepare_ConvergeOnRealTime()
        {
            var (host, client, hostNetwork, clientNetwork, queued) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);

            // Drop the first Prepare, then trigger the host's application-level retry.
            Assert.That(queued.Dequeue(), Is.TypeOf<NotifyRealTimeModeTransition>());
            typeof(MultiplayerHost).GetField("_realTimeModeSwitchDeadline",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(host, DateTime.UtcNow.AddSeconds(-1));
            host.PublishRealTimeCombatSnapshot();

            var prepare = (NotifyRealTimeModeTransition)queued.Dequeue();
            DeliverToClient(clientNetwork, prepare);
            DeliverToClient(clientNetwork, prepare); // Duplicate arrives before either ACK reaches the host.
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(host.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.RealTimeWithPause));
            Assert.That(client.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.RealTimeWithPause));
            Assert.That(host.Game.RealTimeCombat.CanonicalStateHash, Is.EqualTo(client.Game.RealTimeCombat.CanonicalStateHash));
        }

        [Test]
        public async Task TwoActors_LostAndDuplicateCommit_ConvergeOnRealTime()
        {
            var (host, client, hostNetwork, clientNetwork, queued) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            DeliverToClient(clientNetwork, (NotifyRealTimeModeTransition)queued.Dequeue());
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeModeTransitionAck>(hostNetwork)(42,
                (ClientRealTimeModeTransitionAck)queued.Dequeue());

            Assert.That(((NotifyRealTimeModeTransition)queued.Dequeue()).Stage,
                Is.EqualTo((int)RealTimeModeTransitionStage.Commit)); // Drop the first Commit.
            typeof(MultiplayerHost).GetField("_realTimeModeSwitchDeadline",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(host, DateTime.UtcNow.AddSeconds(-1));
            host.PublishRealTimeCombatSnapshot();

            var commit = (NotifyRealTimeModeTransition)queued.Dequeue();
            DeliverToClient(clientNetwork, commit);
            DeliverToClient(clientNetwork, commit);
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(host.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.RealTimeWithPause));
            Assert.That(client.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.RealTimeWithPause));
        }

        [Test]
        public async Task TwoActors_LostRepairPrepare_ResynchronizeAndResume()
        {
            var (host, client, hostNetwork, clientNetwork, queued) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));

            await host.BeginRealTimeRepairAsync();
            Assert.That(((NotifyRealTimeRepair)queued.Dequeue()).Stage,
                Is.EqualTo((int)RealTimeRepairStage.Prepare)); // Drop first Prepare.
            typeof(MultiplayerHost).GetField("_realTimeRepairDeadline",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(host, DateTime.UtcNow.AddSeconds(-1));
            host.PublishRealTimeCombatSnapshot();
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(host.Game.RealTimeCombat.Epoch, Is.EqualTo(client.Game.RealTimeCombat.Epoch));
        }

        private static (MultiplayerHost Host, MultiplayerClient Client, INetworkHostConnection HostNetwork,
            INetworkClientConnection ClientNetwork, Queue<object> Queued) CreatePair()
        {
            var mapper = new MapperConfiguration(x =>
            {
                x.AddProfile<NetworkingProfile>();
                x.AddProfile<NetworkMessagesProfile>();
            }).CreateMapper();
            var hostGame = A.Fake<IGameInteractionService>();
            var clientGame = A.Fake<IGameInteractionService>();
            var hostCombat = A.Fake<ICombatInteractionService>();
            var clientCombat = A.Fake<ICombatInteractionService>();
            A.CallTo(() => hostGame.SetPauseAsync(true)).Returns(Task.CompletedTask);
            A.CallTo(() => clientGame.SetPauseAsync(true)).Returns(Task.CompletedTask);
            A.CallTo(() => hostGame.SetTurnBasedCombatModeAsync(false)).Returns(Task.CompletedTask);
            A.CallTo(() => clientGame.SetTurnBasedCombatModeAsync(false)).Returns(Task.CompletedTask);
            A.CallTo(() => hostCombat.GetRealTimeCombatState()).Returns(new NetworkCombatState());
            A.CallTo(() => clientCombat.GetRealTimeCombatStateAsync()).Returns(Task.FromResult(new NetworkCombatState()));
            A.CallTo(() => clientCombat.UpdateCombatStateAsync(A<NetworkCombatState>._,
                A<List<NetworkAreaEffect>>._, false)).Returns(Task.CompletedTask);
            var mainThread = A.Fake<IMainThreadAccessor>();
            A.CallTo(() => mainThread.Post(A<Action>._)).Invokes(call => ((Action)call.Arguments[0])());

            var hostNetwork = A.Fake<INetworkHostConnection>();
            var clientNetwork = A.Fake<INetworkClientConnection>();
            Fake.GetFakeManager(hostNetwork).AddRuleFirst(new NetworkReceiverFakeRule<INetworkConnection>());
            Fake.GetFakeManager(clientNetwork).AddRuleFirst(new NetworkReceiverFakeRule<INetworkConnection>());
            A.CallTo(() => hostNetwork.IsActive).Returns(true);
            A.CallTo(() => clientNetwork.IsActive).Returns(true);
            var queued = new Queue<object>();
            A.CallTo(() => hostNetwork.Broadcast(A<object>._)).Invokes(call => queued.Enqueue(call.Arguments[0]));
            A.CallTo(() => clientNetwork.Broadcast(A<object>._)).Invokes(call => queued.Enqueue(call.Arguments[0]));

            var leveling = A.Fake<ILevelingInteractionService>();
            var notification = A.Fake<IPlayerNotificationService>();
            var dialog = A.Fake<IDialogInteractionService>();
            var globalMap = A.Fake<IGlobalMapInteractionService>();
            var ping = A.Fake<IPingInteractionService>();
            var settings = A.Fake<IMultiplayerSettingsService>();
            var files = A.Fake<IFileSystemService>();
            var values = A.Fake<IValueGenerator>();
            var host = new MultiplayerHost(A.Fake<ILogger<MultiplayerHost>>(), hostGame, leveling, notification,
                dialog, globalMap, ping, hostCombat, settings, files, hostNetwork, values,
                A.Fake<IHashService>(), mapper, mainThread);
            var client = new MultiplayerClient(A.Fake<ILogger<MultiplayerClient>>(), clientGame, leveling,
                notification, dialog, globalMap, ping, clientCombat, settings, files, clientNetwork, values, mapper);
            host.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = NetworkConstants.HostPlayerId,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running }
            };
            host.Game.Players.Add(new NetworkPlayer(NetworkConstants.HostPlayerId) { LobbySyncStatus = NetworkLobbySyncStatus.Succeed });
            host.Game.Players.Add(new NetworkPlayer(42) { LobbySyncStatus = NetworkLobbySyncStatus.Succeed });
            client.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running }
            };
            return (host, client, hostNetwork, clientNetwork, queued);
        }

        private static void Pump(Queue<object> queued, INetworkHostConnection hostNetwork,
            INetworkClientConnection clientNetwork)
        {
            var delivered = 0;
            while (queued.Count > 0)
            {
                Assert.That(++delivered, Is.LessThan(20), "The protocol must converge without an unbounded message loop.");
                var message = queued.Dequeue();
                switch (message)
                {
                    case NotifyRealTimeModeTransition transition:
                        DeliverToClient(clientNetwork, transition);
                        break;
                    case ClientRealTimeModeTransitionAck ack:
                        FakeUtils.GetNetworkReceiverHandler<ClientRealTimeModeTransitionAck>(hostNetwork)(42, ack);
                        break;
                    case NotifyRealTimeRepair repair:
                        FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRepair>(clientNetwork)(NetworkConstants.HostPlayerId, repair);
                        break;
                    case ClientRealTimeRepairAck repairAck:
                        FakeUtils.GetNetworkReceiverHandler<ClientRealTimeRepairAck>(hostNetwork)(42, repairAck);
                        break;
                    default:
                        Assert.Fail($"Unexpected protocol message: {message.GetType().Name}");
                        break;
                }
            }
        }

        private static void DeliverToClient(INetworkClientConnection network, NotifyRealTimeModeTransition message) =>
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeModeTransition>(network)(NetworkConstants.HostPlayerId, message);
    }
}
