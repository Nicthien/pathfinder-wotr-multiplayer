using System;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Area;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class MultiplayerHostTests
    {
        private NetworkGame SessionRecoveryGame()
        {
            A.CallTo(() => _networkServer.IsActive).Returns(true);
            var game = new NetworkGame(new NetworkGameStartUp()) { Id = "game", LoadedSaveSeed = 17,
                LocalPlayerId = -1, Stage = NetworkLobbyStage.Playing,
                CurrentArea = new NetworkArea { Id = "market" } };
            game.Players.Add(new NetworkPlayer(-1) { LobbySyncStatus = NetworkLobbySyncStatus.Succeed });
            game.Players.Add(new NetworkPlayer(1) { LobbySyncStatus = NetworkLobbySyncStatus.Succeed });
            _multiplayerHost.Game = game;
            return game;
        }
        [Test]
        public async System.Threading.Tasks.Task ManualSync_RetriesMissingTurnAndRateLimitsRepeatedClicks()
        {
            var game = SessionRecoveryGame();
            game.Combat = new NetworkCombat { Stage = NetworkCombatStage.Running,
                Turn = new NetworkCombatTurn { UnitId = "enemy", Stage = NetworkCombatTurnStage.Starting } };
            game.Combat.PlayersNextTurnInitialization.TryAdd("enemy", new() { -1 });
            Assert.That(await _multiplayerHost.ForceCombatSynchronizationAsync(), Is.True);
            Assert.That(await _multiplayerHost.ForceCombatSynchronizationAsync(), Is.False);
            A.CallTo(() => _networkServer.Send(1, A<NotifyInvalidCombatTurnStarted>._)).MustHaveHappenedOnceExactly();
            Assert.That(game.Combat.Turn.Stage, Is.EqualTo(NetworkCombatTurnStage.Starting));
        }
        [Test]
        public async System.Threading.Tasks.Task ManualSync_DoesNotInterruptPlayingTurnOrInventVictory()
        {
            var game = SessionRecoveryGame();
            game.Combat = new NetworkCombat { Stage = NetworkCombatStage.Running,
                Turn = new NetworkCombatTurn { UnitId = "enemy", Stage = NetworkCombatTurnStage.Playing } };
            Assert.That(await _multiplayerHost.ForceCombatSynchronizationAsync(), Is.False);
            Assert.That(game.Combat.Turn.Stage, Is.EqualTo(NetworkCombatTurnStage.Playing));
        }
        [Test]
        public async System.Threading.Tasks.Task ManualSync_NeverRetransmitsPreviousEpochBarrier()
        {
            var game = SessionRecoveryGame();
            game.Combat = new NetworkCombat { Stage = NetworkCombatStage.Running };
            game.RealTimeCombat.Epoch = 4;
            game.RealTimeCombat.Phase = WOTRMultiplayer.Entities.Combat.RealTime.CombatSynchronizationPhase.Synchronizing;
            typeof(WOTRMultiplayer.Services.MultiplayerHost).GetField("_lastRealTimeModeComplete",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(_multiplayerHost, new NotifyRealTimeModeTransition { Epoch = 3 });
            Assert.That(await _multiplayerHost.ForceCombatSynchronizationAsync(), Is.False);
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeModeTransition>._)).MustNotHaveHappened();
        }
        [Test]
        public void SessionRecovery_EarlyAreaAckCanBeRetriedAfterHostLoading()
        {
            var game = SessionRecoveryGame();
            game.Stage = NetworkLobbyStage.Loading;
            game.ForcedPause = new NetworkForcedPause { Reason = NetworkForcedPauseReason.AreaLoading };
            var receive = FakeUtils.GetNetworkReceiverHandler<ClientAreaLoaded>(_networkServer);
            var ack = new ClientAreaLoaded { SessionId = "game", LoadedSaveSeed = 17, AreaId = "market" };
            receive(1, ack);
            Assert.That(game.ForcedPause.ReadyPlayers, Does.Not.Contain(1L));
            game.Stage = NetworkLobbyStage.Playing;
            receive(1, ack);
            Assert.That(game.ForcedPause.ReadyPlayers, Does.Contain(1L));
            game.ForcedPause = null;
            receive(1, ack);
            Assert.That(game.ForcedPause, Is.Null, "A duplicate must not create another loading pause.");
        }
        [TestCase("old", 17, "market")]
        [TestCase("game", 16, "market")]
        [TestCase("game", 17, "old-area")]
        public void SessionRecovery_StaleAreaAckNeverReleasesPause(string id, int seed, string area)
        {
            var game = SessionRecoveryGame();
            game.ForcedPause = new NetworkForcedPause { Reason = NetworkForcedPauseReason.AreaLoading };
            FakeUtils.GetNetworkReceiverHandler<ClientAreaLoaded>(_networkServer)(1,
                new ClientAreaLoaded { SessionId = id, LoadedSaveSeed = seed, AreaId = area });
            Assert.That(game.ForcedPause.ReadyPlayers, Is.Empty);
        }
        [Test]
        public void SessionRecovery_MissingEnemyTurnRequestsInitializationWithoutFabricatingAck()
        {
            var game = SessionRecoveryGame();
            game.Combat = new NetworkCombat { Seed = 21, Round = 2, Stage = NetworkCombatStage.Running,
                Turn = new NetworkCombatTurn { UnitId = "enemy", Stage = NetworkCombatTurnStage.Starting } };
            game.Combat.PlayersNextTurnInitialization.TryAdd("enemy", new() { -1 });
            _multiplayerHost.PublishRealTimeCombatSnapshot();
            Assert.That(game.Combat.Turn.Stage, Is.EqualTo(NetworkCombatTurnStage.Starting));
            Assert.That(game.Combat.PlayersNextTurnInitialization["enemy"], Does.Not.Contain(1L));
            A.CallTo(() => _networkServer.Send(1, A<NotifyInvalidCombatTurnStarted>.That.Matches(m =>
                m.UnitId == "enemy" && m.CombatSeed == 21 && m.LoadedSaveSeed == 17 && m.Round == 2)))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void SessionRecovery_WrongClientRoundRequestsAuthoritativeTurnAgain()
        {
            var game = SessionRecoveryGame();
            game.Combat = new NetworkCombat { Seed = 21, Round = 0, Stage = NetworkCombatStage.Running,
                Turn = new NetworkCombatTurn { UnitId = "enemy", Stage = NetworkCombatTurnStage.Starting } };
            game.Combat.PlayersNextTurnInitialization.TryAdd("enemy", new() { -1 });

            FakeUtils.GetNetworkReceiverHandler<ClientCombatTurnStarted>(_networkServer)(1,
                new ClientCombatTurnStarted { UnitId = "enemy", Round = 1 });

            Assert.That(game.Combat.PlayersNextTurnInitialization["enemy"], Does.Not.Contain(1L));
            A.CallTo(() => _networkServer.Send(1, A<NotifyInvalidCombatTurnStarted>.That.Matches(message =>
                message.UnitId == "enemy" && message.Round == 0 && message.CombatSeed == 21)))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void CombatInitialization_WaitsUntilAreaLoadingBarrierIsReleased()
        {
            var game = SessionRecoveryGame();
            game.CurrentArea.Seed = 12;
            game.Combat = new NetworkCombat { Stage = NetworkCombatStage.Initiating };
            game.ForcedPause = new NetworkForcedPause { Reason = NetworkForcedPauseReason.AreaLoading };

            Assert.That(_multiplayerHost.CanInitializeCombat(), Is.False);
            Assert.That(game.Combat.IsInitiated, Is.False);
            A.CallTo(() => _networkServer.Broadcast(A<NotifyCombatInitiated>._)).MustNotHaveHappened();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SessionRecovery_RejoinWaitsWithoutFreezingTurnBasedHostAndExpiresWithSession(bool replaceSession)
        {
            var game = SessionRecoveryGame();
            game.Combat = new NetworkCombat { Stage = NetworkCombatStage.Running };
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action a) => a());
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(WOTRMultiplayer.Services.MultiplayerHost).GetMethod("QueueSessionRejoin", flags)
                .Invoke(_multiplayerHost, new object[] { 1L, null });
            if (replaceSession) _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp());
            typeof(WOTRMultiplayer.Services.MultiplayerHost).GetMethod("TickSessionRejoin", flags)
                .Invoke(_multiplayerHost, null);
            var pending = typeof(WOTRMultiplayer.Services.MultiplayerHost).GetField("_pendingRejoinSession", flags)
                .GetValue(_multiplayerHost);
            if (replaceSession) Assert.That(pending, Is.Null); else Assert.That(pending, Is.SameAs(game));
            A.CallTo(() => _gameInteractionService.SetPause(true)).MustNotHaveHappened();
        }
    }
}
