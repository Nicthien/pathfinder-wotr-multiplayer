using System;
using System.Reflection;
using System.Threading.Tasks;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Leveling;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class MultiplayerClientTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void ForwardingMustNotAuthorizeAnotherPlayersLevelingChoice(bool sentByOwner)
        {
            var host = new NetworkPlayer(-1) { IsHost = true };
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LocalPlayerId = 42, Players = [host, new NetworkPlayer(42)],
                Characters = [new NetworkCharacter { UnitId = "host-unit", Owner = host }]
            };
            var start = LevelingStart("host-unit", NetworkLevelingType.MythicLeveling);
            start.IsForced = true;
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingStarted>(_networkClient)(-1, start);
            _multiplayerClient.OnForceLevelingUI("host-unit", NetworkLevelingType.MythicLeveling);
            var server = A.Fake<WOTRMultiplayer.Networking.Abstractions.TCP.INetworkServer>();
            A.CallTo(() => server.IsActive).Returns(true);
            var external = A.Fake<WOTRMultiplayer.Networking.Abstractions.ExternalConnections.IExternalConnectionService>();
            var connection = new WOTRMultiplayer.Networking.NetworkHostConnection(
                A.Fake<Microsoft.Extensions.Logging.ILogger<WOTRMultiplayer.Networking.NetworkHostConnection>>(),
                A.Fake<WOTRMultiplayer.Networking.Abstractions.IMessageConsumer>(), server, external);
            server.OnClientConnected(123);
            var forged = new NotifyLevelingNameChanged { Name = "forged", Leveling = start.Leveling };
            A.CallTo(() => server.BroadcastExcept(123, forged)).Invokes(() =>
                FakeUtils.GetNetworkReceiverHandler<NotifyLevelingNameChanged>(_networkClient)(-1, forged));
            A.CallTo(() => server.Broadcast(forged)).Invokes(() =>
                FakeUtils.GetNetworkReceiverHandler<NotifyLevelingNameChanged>(_networkClient)(-1, forged));
            if (sentByOwner) connection.Broadcast(forged);
            else server.OnMessageReceived(new WOTRMultiplayer.Networking.Consuming.NetworkMessageMetadata(
                WOTRMultiplayer.Networking.Channels.NetworkChannelType.TCP, 123, forged));
            if (sentByOwner)
                A.CallTo(() => _levelingInteractionService.SetLevelingName("forged")).MustHaveHappenedOnceExactly();
            else
                A.CallTo(() => _levelingInteractionService.SetLevelingName("forged")).MustNotHaveHappened();
            GC.KeepAlive(connection);
        }
    }

    public partial class MultiplayerHostTests
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task AreaResetAfterTerminalApplicationRetainsOnlyRealFailures(bool cancel, bool failAfter)
        {
            var owner = new NetworkPlayer(42);
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action action) => action());
            A.CallTo(() => _levelingInteractionService.IsBackgroundLevelingReady).Returns(true);
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LoadedSaveSeed = 17, LocalPlayerId = -1,
                Players = [new NetworkPlayer(-1) { IsHost = true }, owner],
                Characters = [new NetworkCharacter { UnitId = "client-unit", Owner = owner }]
            };
            Action finish = () =>
            {
                if (cancel) _multiplayerHost.OnLevelingTerminated();
                else _multiplayerHost.OnLevelingCompleted();
                typeof(MultiplayerActorBase).GetMethod("SoftReset", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(_multiplayerHost, null);
                if (failAfter) throw new InvalidOperationException("failure-after-terminal-callback");
            };
            A.CallTo(() => _levelingInteractionService.CompleteLeveling()).Invokes(finish);
            A.CallTo(() => _levelingInteractionService.TerminateLeveling()).Invokes(finish);
            NetworkLevelingIdentity identity = null;
            A.CallTo(() => _networkServer.Broadcast(A<NotifyLevelingStarted>._))
                .Invokes((object message) => identity = ((NotifyLevelingStarted)message).Leveling);
            FakeUtils.GetNetworkReceiverHandler<ClientCharacterLevelingRequested>(_networkServer)(42,
                new ClientCharacterLevelingRequested { UnitId = "client-unit", Type = "Leveling" });
            if (cancel)
                FakeUtils.GetNetworkReceiverHandler<NotifyLevelingTerminated>(_networkServer)(42,
                    new NotifyLevelingTerminated { Leveling = identity });
            else
                FakeUtils.GetNetworkReceiverHandler<NotifyLevelingCompleted>(_networkServer)(42,
                    new NotifyLevelingCompleted { Leveling = identity });
            await Task.Delay(300);
            Assert.That(_multiplayerHost.IsSavingBlockedByCombatSync, Is.EqualTo(failAfter));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void OwnerChoicesAreRelayedOnlyAfterValidation(bool deferred, bool legitimateOwner)
        {
            var owner = new NetworkPlayer(42);
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action action) => action());
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LocalPlayerId = -1,
                Players = [new NetworkPlayer(-1) { IsHost = true }, owner, new NetworkPlayer(99)],
                Characters = [new NetworkCharacter { UnitId = "client-unit", Owner = owner }]
            };
            NetworkLevelingIdentity identity = null;
            A.CallTo(() => _networkServer.Broadcast(A<NotifyLevelingStarted>._))
                .Invokes((object message) => identity = ((NotifyLevelingStarted)message).Leveling);
            FakeUtils.GetNetworkReceiverHandler<ClientCharacterLevelingRequested>(_networkServer)(42,
                new ClientCharacterLevelingRequested { UnitId = "client-unit", Type = "Leveling" });
            if (!deferred)
            {
                _multiplayerHost.Game.Leveling = new NetworkLeveling("client-unit", NetworkLevelingType.Leveling)
                    { Identity = identity, IsForced = true };
            }
            var choice = new NotifyLevelingNameChanged { Name = "choice", Leveling = identity };
            var sender = legitimateOwner ? 42 : 99;
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingNameChanged>(_networkServer)(sender, choice);
            if (legitimateOwner)
                A.CallTo(() => _networkServer.BroadcastExcept(sender, choice)).MustHaveHappenedOnceExactly();
            else
                A.CallTo(() => _networkServer.BroadcastExcept(A<long>._, choice)).MustNotHaveHappened();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RelayedPhaseWitnessMustNameItsActualSender(bool authentic)
        {
            var owner = new NetworkPlayer(42);
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action action) => action());
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LocalPlayerId = -1,
                Players = [new NetworkPlayer(-1) { IsHost = true }, owner, new NetworkPlayer(99)],
                Characters = [new NetworkCharacter { UnitId = "client-unit", Owner = owner }]
            };
            NetworkLevelingIdentity identity = null;
            A.CallTo(() => _networkServer.Broadcast(A<NotifyLevelingStarted>._))
                .Invokes((object message) => identity = ((NotifyLevelingStarted)message).Leveling);
            FakeUtils.GetNetworkReceiverHandler<ClientCharacterLevelingRequested>(_networkServer)(42,
                new ClientCharacterLevelingRequested { UnitId = "client-unit", Type = "Leveling" });
            var witness = new NotifyLevelingPhaseWitnessed { Leveling = identity, PlayerId = authentic ? 99 : 42 };
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingPhaseWitnessed>(_networkServer)(99, witness);
            if (authentic)
                A.CallTo(() => _networkServer.BroadcastExcept(99, witness)).MustHaveHappenedOnceExactly();
            else
                A.CallTo(() => _networkServer.BroadcastExcept(A<long>._, witness)).MustNotHaveHappened();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CompletedReplayFollowedByAreaResetDoesNotLatchFailure(bool resetArea)
        {
            var host = new NetworkPlayer(-1) { IsHost = true };
            var client = new NetworkPlayer(42);
            A.CallTo(() => _networkServer.IsActive).Returns(true);
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action action) => action());
            A.CallTo(() => _levelingInteractionService.IsBackgroundLevelingReady).Returns(true);
            A.CallTo(() => _levelingInteractionService.CompleteLeveling()).Invokes(() =>
            {
                _multiplayerHost.OnLevelingCompleted();
                Assert.That(_multiplayerHost.Game.Leveling, Is.Null);
                if (resetArea)
                    typeof(MultiplayerActorBase).GetMethod("SoftReset", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(_multiplayerHost, null);
            });
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LoadedSaveSeed = 17, LocalPlayerId = -1, Players = [host, client],
                Characters = [new NetworkCharacter { UnitId = "client-unit", Owner = client }]
            };
            NetworkLevelingIdentity identity = null;
            A.CallTo(() => _networkServer.Broadcast(A<NotifyLevelingStarted>._))
                .Invokes((object message) => identity = ((NotifyLevelingStarted)message).Leveling);
            FakeUtils.GetNetworkReceiverHandler<ClientCharacterLevelingRequested>(_networkServer)(42,
                new ClientCharacterLevelingRequested { UnitId = "client-unit", Type = "Leveling" });
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingCompleted>(_networkServer)(42,
                new NotifyLevelingCompleted { Leveling = identity });
            await Task.Delay(300);
            Assert.That(_multiplayerHost.IsSavingBlockedByCombatSync, Is.False,
                "A completed remote progression must not become a permanent synchronization failure on area reset");
        }
    }
}
