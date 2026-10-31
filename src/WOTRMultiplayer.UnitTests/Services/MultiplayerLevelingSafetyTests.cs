using System;
using System.Collections.Generic;
using System.Reflection;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Abstractions.Unity;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Leveling;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class MultiplayerClientTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void StartNotificationIsRevalidatedOnTheGameThread(bool replaceSession)
        {
            var client = new NetworkPlayer(42);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LocalPlayerId = 42,
                Players = [new NetworkPlayer(-1) { IsHost = true }, client],
                Characters = [new NetworkCharacter { UnitId = "client-unit", Owner = client }]
            };
            var message = LevelingStart("client-unit", NetworkLevelingType.Leveling);
            var queued = new Queue<Action>();
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action action) => queued.Enqueue(action));
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingStarted>(_networkClient)(-1, message);
            if (replaceSession) _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp()) { Id = "new-session" };
            while (queued.Count > 0) queued.Dequeue()();
            if (replaceSession)
            {
                Assert.That(_multiplayerClient.Game.Leveling, Is.Null);
                A.CallTo(() => _levelingInteractionService.StartLeveling(A<string>._, A<NetworkLevelingType>._)).MustNotHaveHappened();
            }
            else
            {
                Assert.That(_multiplayerClient.Game.Leveling.Identity.Id, Is.EqualTo(message.Leveling.Id));
                A.CallTo(() => _levelingInteractionService.StartLeveling("client-unit", NetworkLevelingType.Leveling))
                    .MustHaveHappenedOnceExactly();
            }
        }

        [Test]
        public void LiveApplicationFailureKeepsSavingBlockedAfterProgressionIsCleared()
        {
            var client = new NetworkPlayer(42);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LoadedSaveSeed = 17, LocalPlayerId = 42,
                Players = [new NetworkPlayer(-1) { IsHost = true }, client],
                Characters = [new NetworkCharacter { UnitId = "client-unit", Owner = client }]
            };
            var start = LevelingStart("client-unit", NetworkLevelingType.Leveling);
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingStarted>(_networkClient)(-1, start);
            var queued = new Queue<Action>();
            var dispatcher = A.Fake<IMainThreadAccessor>();
            A.CallTo(() => dispatcher.Post(A<Action>._)).Invokes((Action action) => queued.Enqueue(action));
            var ui = A.Fake<IUIAccessor>();
            A.CallTo(() => ui.CharGenView).Throws(new InvalidOperationException("native-application-failed"));
            var service = new LevelingInteractionService(A.Fake<ILogger<LevelingInteractionService>>(), dispatcher,
                ui, A.Fake<IPlayerNotificationService>(), A.Fake<IGameStateLookupService>(), A.Fake<IUISyncCountersService>());
            typeof(MultiplayerActorBase).GetProperty("LevelingInteraction", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_multiplayerClient, service);
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingNameChanged>(_networkClient)(-1,
                new NotifyLevelingNameChanged { Name = "name", Leveling = start.Leveling });
            Assert.Throws<InvalidOperationException>(() => queued.Dequeue()());
            _multiplayerClient.Game.Leveling = null;
            Assert.That(_multiplayerClient.IsSavingBlockedByCombatSync, Is.True);
            A.CallTo(() => _gameInteractionService.SetPause(true)).MustHaveHappened();
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void ForcedIdentitySurvivesBothArrivalOrders(bool nativeFirst, bool localOwner)
        {
            var host = new NetworkPlayer(-1) { IsHost = true };
            var client = new NetworkPlayer(42);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "review-session", LoadedSaveSeed = 17, LocalPlayerId = 42, Players = [host, client],
                Characters = [new NetworkCharacter { UnitId = "host-unit", Owner = localOwner ? client : host }]
            };
            var start = LevelingStart("host-unit", NetworkLevelingType.MythicLeveling);
            start.IsForced = true;
            if (nativeFirst) _multiplayerClient.OnForceLevelingUI("host-unit", NetworkLevelingType.MythicLeveling);
            if (nativeFirst) Assert.That(_multiplayerClient.CanMakeLevelingDecisions(), Is.False);
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingStarted>(_networkClient)(-1, start);
            if (!nativeFirst) _multiplayerClient.OnForceLevelingUI("host-unit", NetworkLevelingType.MythicLeveling);
            Assert.That(_multiplayerClient.Game.Leveling.Identity.Id, Is.EqualTo(start.Leveling.Id));
            Assert.That(_multiplayerClient.Game.Leveling.Identity.OwnerId, Is.EqualTo(localOwner ? 42 : -1));
            Assert.That(_multiplayerClient.Game.Leveling.AwaitingHostIdentity, Is.False);
            // Repeated native callbacks cannot put an acknowledged identity back into waiting.
            _multiplayerClient.OnForceLevelingUI("host-unit", NetworkLevelingType.MythicLeveling);
            Assert.That(_multiplayerClient.Game.Leveling.AwaitingHostIdentity, Is.False);
        }

        [Test]
        public void ForcedChoicesReceivedBeforeNativeOpeningWaitAndDrainInOrder()
        {
            var host = new NetworkPlayer(-1) { IsHost = true };
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LocalPlayerId = 42, Players = [host, new NetworkPlayer(42)],
                Characters = [new NetworkCharacter { UnitId = "host-unit", Owner = host }]
            };
            var start = LevelingStart("host-unit", NetworkLevelingType.MythicLeveling);
            start.IsForced = true;
            var calls = new List<string>();
            A.CallTo(() => _levelingInteractionService.SelectMythicLevelingClass("path"))
                .Invokes(() => calls.Add("choice"));
            A.CallTo(() => _levelingInteractionService.CompleteLeveling()).Invokes(() =>
            {
                calls.Add("complete");
                _multiplayerClient.Game.Leveling = null;
            });
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingStarted>(_networkClient)(-1, start);
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingMythicClassSelected>(_networkClient)(-1,
                new NotifyLevelingMythicClassSelected { Leveling = start.Leveling, MythicClassId = "path" });
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingCompleted>(_networkClient)(-1,
                new NotifyLevelingCompleted { Leveling = start.Leveling });
            Assert.That(calls, Is.Empty);
            Assert.That(_multiplayerClient.IsSavingBlockedByCombatSync, Is.True);
            A.CallTo(() => _levelingInteractionService.StartBackgroundLeveling(A<string>._, A<NetworkLevelingType>._))
                .MustNotHaveHappened();
            _multiplayerClient.OnForceLevelingUI("host-unit", NetworkLevelingType.MythicLeveling);
            Assert.That(calls, Is.EqualTo(new[] { "choice", "complete" }));
            Assert.That(_multiplayerClient.IsSavingBlockedByCombatSync, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LiveLevelingDispatchRejectsReplacedSession(bool replaceSession)
        {
            var host = new NetworkPlayer(-1) { IsHost = true };
            var client = new NetworkPlayer(42);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "review-session", LoadedSaveSeed = 17, LocalPlayerId = 42, Players = [host, client],
                Characters = [new NetworkCharacter { UnitId = "client-unit", Owner = client }]
            };
            var start = LevelingStart("client-unit", NetworkLevelingType.Leveling);
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingStarted>(_networkClient)(-1, start);
            var queued = new Queue<Action>();
            var dispatcher = A.Fake<IMainThreadAccessor>();
            A.CallTo(() => dispatcher.Post(A<Action>._)).Invokes((Action action) => queued.Enqueue(action));
            var ui = A.Fake<IUIAccessor>();
            var realService = new LevelingInteractionService(A.Fake<ILogger<LevelingInteractionService>>(),
                dispatcher, ui, A.Fake<IPlayerNotificationService>(), A.Fake<IGameStateLookupService>(),
                A.Fake<IUISyncCountersService>());
            typeof(MultiplayerActorBase).GetProperty("LevelingInteraction", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_multiplayerClient, realService);
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingNameChanged>(_networkClient)(-1,
                new NotifyLevelingNameChanged { Name = "old-name", Leveling = start.Leveling });
            Assert.That(queued.Count, Is.EqualTo(1));
            if (replaceSession) _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp()) { Id = "new-session" };
            if (replaceSession) Assert.DoesNotThrow(() => queued.Dequeue()());
            else Assert.Throws<InvalidOperationException>(() => queued.Dequeue()()); // The live scope reports the deliberately missing UI.
            if (replaceSession) A.CallTo(() => ui.CharGenView).MustNotHaveHappened();
            else A.CallTo(() => ui.CharGenView).MustHaveHappened();
        }
    }

    public partial class MultiplayerHostTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async System.Threading.Tasks.Task VerifiedRemoteCompletionOrCancellationReleasesSaveGuard(bool cancel)
        {
            var host = new NetworkPlayer(-1) { IsHost = true };
            var client = new NetworkPlayer(42);
            A.CallTo(() => _networkServer.IsActive).Returns(true);
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action action) => action());
            A.CallTo(() => _levelingInteractionService.IsBackgroundLevelingReady).Returns(true);
            A.CallTo(() => _levelingInteractionService.CompleteLeveling()).Invokes(() => _multiplayerHost.OnLevelingCompleted());
            A.CallTo(() => _levelingInteractionService.TerminateLeveling()).Invokes(() => _multiplayerHost.OnLevelingTerminated());
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LocalPlayerId = -1, Players = [host, client],
                Characters = [new NetworkCharacter { UnitId = "client-unit", Owner = client }]
            };
            NetworkLevelingIdentity identity = null;
            A.CallTo(() => _networkServer.Broadcast(A<NotifyLevelingStarted>._))
                .Invokes((object message) => identity = ((NotifyLevelingStarted)message).Leveling);
            FakeUtils.GetNetworkReceiverHandler<ClientCharacterLevelingRequested>(_networkServer)(42,
                new ClientCharacterLevelingRequested { UnitId = "client-unit", Type = "Leveling" });
            Assert.That(_multiplayerHost.IsSavingBlockedByCombatSync, Is.True);
            if (cancel)
                FakeUtils.GetNetworkReceiverHandler<NotifyLevelingTerminated>(_networkServer)(42,
                    new NotifyLevelingTerminated { Leveling = identity });
            else
                FakeUtils.GetNetworkReceiverHandler<NotifyLevelingCompleted>(_networkServer)(42,
                    new NotifyLevelingCompleted { Leveling = identity });
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (_multiplayerHost.IsSavingBlockedByCombatSync && DateTime.UtcNow < deadline)
                await System.Threading.Tasks.Task.Delay(10);
            Assert.That(_multiplayerHost.IsSavingBlockedByCombatSync, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PendingOrFailedRemoteLevelingBlocksSaving(bool failReplay)
        {
            var host = new NetworkPlayer(-1) { IsHost = true };
            var client = new NetworkPlayer(42);
            A.CallTo(() => _networkServer.IsActive).Returns(true);
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action action) => action());
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "review-session", LoadedSaveSeed = 17, LocalPlayerId = -1, Stage = NetworkLobbyStage.Playing,
                Players = [host, client], Characters = [new NetworkCharacter { UnitId = "client-unit", Owner = client }]
            };
            NetworkLevelingIdentity identity = null;
            A.CallTo(() => _networkServer.Broadcast(A<NotifyLevelingStarted>._))
                .Invokes((object message) => identity = ((NotifyLevelingStarted)message).Leveling);
            FakeUtils.GetNetworkReceiverHandler<ClientCharacterLevelingRequested>(_networkServer)(42,
                new ClientCharacterLevelingRequested { UnitId = "client-unit", Type = "Leveling" });
            Assert.That(identity, Is.Not.Null);
            if (failReplay)
            {
                A.CallTo(() => _levelingInteractionService.StartBackgroundLeveling("client-unit", NetworkLevelingType.Leveling))
                    .Throws(new InvalidOperationException("review-failed-native-start"));
                FakeUtils.GetNetworkReceiverHandler<NotifyLevelingCompleted>(_networkServer)(42,
                    new NotifyLevelingCompleted { Leveling = identity });
            }
            Assert.That(_multiplayerHost.IsSavingBlockedByCombatSync, Is.True);
            if (failReplay)
            {
                typeof(MultiplayerActorBase).GetMethod("SoftReset", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(_multiplayerHost, null);
                Assert.That(_multiplayerHost.IsSavingBlockedByCombatSync, Is.True, "Area reset must not clear a failed replay");
                _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp()) { Id = "fresh-session" };
                Assert.That(_multiplayerHost.IsSavingBlockedByCombatSync, Is.False);
            }
        }
    }
}
