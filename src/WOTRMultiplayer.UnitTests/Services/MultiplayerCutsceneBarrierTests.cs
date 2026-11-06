using FakeItEasy;
using Kingmaker.GameModes;
using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class MultiplayerClientTests
    {
        [Test]
        public void RemoteCutscene_PausesUntilRemoteCutsceneEnds()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Players = [new NetworkPlayer(NetworkConstants.HostPlayerId) { IsHost = true }, new NetworkPlayer(42)]
            };
            A.CallTo(() => _gameInteractionService.IsPaused).Returns(false);
            var start = FakeUtils.GetNetworkReceiverHandler<NotifyGameModeTypeStarted>(_networkClient);
            var end = FakeUtils.GetNetworkReceiverHandler<NotifyGameModeTypeEnded>(_networkClient);

            start(NetworkConstants.HostPlayerId, new NotifyGameModeTypeStarted
            {
                PlayerId = NetworkConstants.HostPlayerId,
                Type = GameModeType.Cutscene.Name
            });
            end(NetworkConstants.HostPlayerId, new NotifyGameModeTypeEnded
            {
                PlayerId = NetworkConstants.HostPlayerId,
                Type = GameModeType.Cutscene.Name
            });

            A.CallTo(() => _gameInteractionService.SetPause(true)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _gameInteractionService.SetPause(false)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void RemoteCutscene_DoesNotReleasePreexistingPause()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Players = [new NetworkPlayer(NetworkConstants.HostPlayerId) { IsHost = true }, new NetworkPlayer(42)]
            };
            A.CallTo(() => _gameInteractionService.IsPaused).Returns(true);
            var start = FakeUtils.GetNetworkReceiverHandler<NotifyGameModeTypeStarted>(_networkClient);
            var end = FakeUtils.GetNetworkReceiverHandler<NotifyGameModeTypeEnded>(_networkClient);

            start(NetworkConstants.HostPlayerId, new NotifyGameModeTypeStarted
            {
                PlayerId = NetworkConstants.HostPlayerId,
                Type = GameModeType.Cutscene.Name
            });
            end(NetworkConstants.HostPlayerId, new NotifyGameModeTypeEnded
            {
                PlayerId = NetworkConstants.HostPlayerId,
                Type = GameModeType.Cutscene.Name
            });

            A.CallTo(() => _gameInteractionService.SetPause(true)).MustHaveHappenedTwiceExactly();
            A.CallTo(() => _gameInteractionService.SetPause(false)).MustNotHaveHappened();
        }

        [Test]
        public void RemoteCutscene_DoesNotPauseAConcurrentLocalCutscene()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Players = [new NetworkPlayer(NetworkConstants.HostPlayerId) { IsHost = true }, new NetworkPlayer(42)]
            };
            A.CallTo(() => _gameInteractionService.IsPaused).Returns(false);
            _multiplayerClient.OnStartGameMode(GameModeType.Cutscene);
            var start = FakeUtils.GetNetworkReceiverHandler<NotifyGameModeTypeStarted>(_networkClient);
            var end = FakeUtils.GetNetworkReceiverHandler<NotifyGameModeTypeEnded>(_networkClient);

            start(NetworkConstants.HostPlayerId, new NotifyGameModeTypeStarted
            {
                PlayerId = NetworkConstants.HostPlayerId,
                Type = GameModeType.Cutscene.Name
            });
            end(NetworkConstants.HostPlayerId, new NotifyGameModeTypeEnded
            {
                PlayerId = NetworkConstants.HostPlayerId,
                Type = GameModeType.Cutscene.Name
            });

            A.CallTo(() => _gameInteractionService.SetPause(true)).MustNotHaveHappened();
            A.CallTo(() => _gameInteractionService.SetPause(false)).MustNotHaveHappened();

            _multiplayerClient.OnStopGameMode(GameModeType.Cutscene);

            A.CallTo(() => _gameInteractionService.SetPause(false)).MustHaveHappenedOnceExactly();
        }
    }

    public partial class MultiplayerHostTests
    {
        [Test]
        public void ClientCutscene_PausesHostUntilClientCutsceneEnds()
        {
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = NetworkConstants.HostPlayerId,
                Players = [new NetworkPlayer(NetworkConstants.HostPlayerId) { IsHost = true }, new NetworkPlayer(42)]
            };
            A.CallTo(() => _gameInteractionService.IsPaused).Returns(false);
            var start = FakeUtils.GetNetworkReceiverHandler<NotifyGameModeTypeStarted>(_networkServer);
            var end = FakeUtils.GetNetworkReceiverHandler<NotifyGameModeTypeEnded>(_networkServer);

            start(42, new NotifyGameModeTypeStarted { PlayerId = 42, Type = GameModeType.Cutscene.Name });
            end(42, new NotifyGameModeTypeEnded { PlayerId = 42, Type = GameModeType.Cutscene.Name });

            A.CallTo(() => _gameInteractionService.SetPause(true)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _gameInteractionService.SetPause(false)).MustHaveHappenedOnceExactly();
        }
    }
}
