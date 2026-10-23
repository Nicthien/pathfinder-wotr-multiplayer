using System;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class MultiplayerClientTests
    {
        [Test]
        public void CombatStart_NativeTurnInitializationAcceptsHostCheckpoint()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp()) { Id = "game", LoadedSaveSeed = 17,
                Combat = new NetworkCombat { Seed = 21, Round = 1, Stage = NetworkCombatStage.Running } };
            Assert.That(_multiplayerClient.OnBeforeTurnStart("enemy", false), Is.False);
            A.CallTo(() => _networkClient.Broadcast(A<ClientCombatTurnStarted>.That.Matches(m => m.UnitId == "enemy")))
                .MustHaveHappenedOnceExactly();
            FakeUtils.GetNetworkReceiverHandler<NotifyCombatTurnStartSynchronizationRequired>(_networkClient)(-1,
                new NotifyCombatTurnStartSynchronizationRequired { SessionId = "game", LoadedSaveSeed = 17,
                    CombatSeed = 21, Round = 1, UnitId = "enemy", CombatState = new() });
            A.CallTo(() => _combatInteractionService.UpdateCombatStateAsync(A<NetworkCombatState>._,
                A<System.Collections.Generic.List<WOTRMultiplayer.Entities.AreaEffects.NetworkAreaEffect>>._, false))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => _networkClient.Broadcast(A<ClientCombatTurnStartSynchronized>._)).MustHaveHappenedOnceExactly();
        }

        [TestCase(NetworkCombatTurnStage.Initialization)]
        [TestCase(NetworkCombatTurnStage.Starting)]
        public void ManualSync_DuplicateStartCheckpointDoesNotReplayAreaEffects(NetworkCombatTurnStage stage)
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp()) { Id = "game", LoadedSaveSeed = 17,
                Combat = new NetworkCombat { Seed = 21, Round = 2, Stage = NetworkCombatStage.Running,
                    Turn = new NetworkCombatTurn { UnitId = "enemy", Stage = stage } } };
            var receive = FakeUtils.GetNetworkReceiverHandler<NotifyCombatTurnStartSynchronizationRequired>(_networkClient);
            var frame = new NotifyCombatTurnStartSynchronizationRequired { SessionId = "game", LoadedSaveSeed = 17,
                CombatSeed = 21, Round = 2, UnitId = "enemy", CombatState = new() };
            receive(-1, frame); receive(-1, frame);
            A.CallTo(() => _combatInteractionService.UpdateCombatStateAsync(A<NetworkCombatState>._,
                A<System.Collections.Generic.List<WOTRMultiplayer.Entities.AreaEffects.NetworkAreaEffect>>._, false))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => _networkClient.Broadcast(A<ClientCombatTurnStartSynchronized>._)).MustHaveHappenedTwiceExactly();
            frame.LoadedSaveSeed = 16;
            receive(-1, frame);
            A.CallTo(() => _networkClient.Broadcast(A<ClientCombatTurnStartSynchronized>._)).MustHaveHappenedTwiceExactly();
        }
        [TestCase(false)]
        [TestCase(true)]
        public void SessionRecovery_MissingTurnDispatchChecksReloadAgain(bool reload)
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            var session = new NetworkGame(new NetworkGameStartUp()) { Id = "game", LoadedSaveSeed = 17,
                Combat = new NetworkCombat { Seed = 21, Round = 2, Stage = NetworkCombatStage.Running } };
            _multiplayerClient.Game = session;
            Action dispatch = null;
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action a) => dispatch = a);
            FakeUtils.GetNetworkReceiverHandler<NotifyInvalidCombatTurnStarted>(_networkClient)(-1,
                new NotifyInvalidCombatTurnStarted { SessionId = "game", LoadedSaveSeed = 17,
                    CombatSeed = 21, Round = 2, UnitId = "enemy" });
            Assert.That(dispatch, Is.Not.Null);
            if (reload) session.LoadedSaveSeed++;
            dispatch();
            var call = A.CallTo(() => _combatInteractionService.StartTurnBasedCombatTurn("enemy", A<Action>._, A<Func<bool>>._));
            if (reload) call.MustNotHaveHappened(); else call.MustHaveHappenedOnceExactly();
        }

        [TestCase("old", 17, 21, 2, -1)]
        [TestCase("game", 16, 21, 2, -1)]
        [TestCase("game", 17, 20, 2, -1)]
        [TestCase("game", 17, 21, 1, -1)]
        [TestCase("game", 17, 21, 2, 1)]
        public void SessionRecovery_RejectsStaleOrNonHostTurn(string id, int save, int combat, int round, long sender)
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp()) { Id = "game", LoadedSaveSeed = 17,
                Combat = new NetworkCombat { Seed = 21, Round = 2, Stage = NetworkCombatStage.Running } };
            FakeUtils.GetNetworkReceiverHandler<NotifyInvalidCombatTurnStarted>(_networkClient)(sender,
                new NotifyInvalidCombatTurnStarted { SessionId = id, LoadedSaveSeed = save,
                    CombatSeed = combat, Round = round, UnitId = "enemy" });
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).MustNotHaveHappened();
        }

        [Test]
        public void SessionRecovery_DuplicateRequestResendsAckWithoutRestartingTurn()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp()) { Id = "game", LoadedSaveSeed = 17,
                Combat = new NetworkCombat { Seed = 21, Round = 2, Stage = NetworkCombatStage.Running,
                    Turn = new NetworkCombatTurn { UnitId = "enemy", Stage = NetworkCombatTurnStage.Starting } } };
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action a) => a());
            var receive = FakeUtils.GetNetworkReceiverHandler<NotifyInvalidCombatTurnStarted>(_networkClient);
            var frame = new NotifyInvalidCombatTurnStarted { SessionId = "game", LoadedSaveSeed = 17,
                CombatSeed = 21, Round = 2, UnitId = "enemy" };
            receive(-1, frame); receive(-1, frame);
            A.CallTo(() => _networkClient.Broadcast(A<ClientCombatTurnStarted>.That.Matches(m => m.UnitId == "enemy")))
                .MustHaveHappenedTwiceExactly();
            A.CallTo(() => _combatInteractionService.StartTurnBasedCombatTurn(A<string>._, A<Action>._, A<Func<bool>>._))
                .MustNotHaveHappened();
        }

        [TestCase(NetworkCombatTurnStage.Initialization)]
        [TestCase(NetworkCombatTurnStage.StartSynchronization)]
        [TestCase(NetworkCombatTurnStage.Playing)]
        public void SessionRecovery_HostCheckpointReplacesStaleLocalTurn(NetworkCombatTurnStage localStage)
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp()) { Id = "game", LoadedSaveSeed = 17,
                RealTimeCombat = { Mode = WOTRMultiplayer.Entities.Combat.RealTime.CombatMode.TurnBased,
                    Phase = WOTRMultiplayer.Entities.Combat.RealTime.CombatSynchronizationPhase.Stable },
                Combat = new NetworkCombat { Seed = 21, Round = 2, Stage = NetworkCombatStage.Running,
                    Turn = new NetworkCombatTurn { UnitId = "stale-unit", Stage = localStage } } };
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action a) => a());

            FakeUtils.GetNetworkReceiverHandler<NotifyInvalidCombatTurnStarted>(_networkClient)(-1,
                new NotifyInvalidCombatTurnStarted { SessionId = "game", LoadedSaveSeed = 17,
                    CombatSeed = 21, Round = 2, UnitId = "host-unit" });

            A.CallTo(() => _combatInteractionService.StartTurnBasedCombatTurn("host-unit", A<Action>._, A<Func<bool>>._))
                .MustHaveHappenedOnceExactly();
            Assert.That(_multiplayerClient.Game.Combat.Turn, Is.Null);
        }

        [Test]
        public void CombatSaveAlignment_ReplacesStalePlayingTurnBeforeDigestBarrier()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action action) => action());
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "game", SessionSeed = 13, LoadedSaveSeed = 17,
                Stage = NetworkLobbyStage.Playing,
                RealTimeCombat =
                {
                    Mode = WOTRMultiplayer.Entities.Combat.RealTime.CombatMode.TurnBased,
                    Phase = WOTRMultiplayer.Entities.Combat.RealTime.CombatSynchronizationPhase.Stable,
                    Epoch = 8
                },
                Combat = new NetworkCombat
                {
                    Seed = 21, Round = 2, Stage = NetworkCombatStage.Running,
                    Turn = new NetworkCombatTurn
                    {
                        UnitId = "stale-unit", Seed = 40, IsAI = false,
                        Stage = NetworkCombatTurnStage.Playing
                    }
                }
            };

            FakeUtils.GetNetworkReceiverHandler<NotifyCombatSave>(_networkClient)(-1,
                new NotifyCombatSave
                {
                    SessionId = "game", SessionSeed = 13, LoadedSaveSeed = 17, Epoch = 8,
                    Operation = Guid.NewGuid().ToString("N"), Stage = "Align",
                    Round = 2, UnitId = "host-unit", TurnSeed = 71
                });

            A.CallTo(() => _combatInteractionService.StartTurnBasedCombatTurn(
                    "host-unit", A<Action>._, A<Func<bool>>._))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => _networkClient.Broadcast(A<ClientCombatSaveAck>._)).MustNotHaveHappened();
        }
    }
}
