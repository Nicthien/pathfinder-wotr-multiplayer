using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Dialogs;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Networking.Messages.Lobby;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class MultiplayerHostTests
    {
        private void PrepareFeatureControlGame(NetworkDialogState dialogState = null)
        {
            A.CallTo(() => _networkServer.IsActive).Returns(true);
            var host = new NetworkPlayer(NetworkConstants.HostPlayerId)
                { IsHost = true, LobbySyncStatus = NetworkLobbySyncStatus.Succeed, Name = "Host" };
            var client = new NetworkPlayer(42)
                { LobbySyncStatus = NetworkLobbySyncStatus.Succeed, Name = "Client" };
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LocalPlayerId = NetworkConstants.HostPlayerId,
                Players = [host, client], DialogState = dialogState
            };
            _multiplayerHost.Game.FeatureControllers[NetworkPlayerControlledFeature.Dialogs] = NetworkConstants.HostPlayerId;
            _multiplayerHost.Game.FeatureControllers[NetworkPlayerControlledFeature.VendorDeal] = NetworkConstants.HostPlayerId;
            _multiplayerHost.Game.FeatureControllers[NetworkPlayerControlledFeature.CrusadeArmyCombat] = NetworkConstants.HostPlayerId;
        }

        [Test]
        public void FeatureControlRequest_TransfersVendorControlThroughHost()
        {
            PrepareFeatureControlGame();
            var receive = FakeUtils.GetNetworkReceiverHandler<ClientFeatureControlRequested>(_networkServer);

            receive(42, new ClientFeatureControlRequested
            {
                Feature = nameof(NetworkPlayerControlledFeature.VendorDeal), SessionId = "session", Sequence = 4
            });

            Assert.That(_multiplayerHost.Game.FeatureControllers[NetworkPlayerControlledFeature.VendorDeal], Is.EqualTo(42));
            Assert.That(_multiplayerHost.Game.FeatureControlTransfers.ContainsKey(NetworkPlayerControlledFeature.VendorDeal), Is.False);
            A.CallTo(() => _networkServer.Broadcast(A<NotifyFeatureControlRequestResolved>.That.Matches(x =>
                x.Feature == nameof(NetworkPlayerControlledFeature.VendorDeal) && x.RequesterId == 42 && x.Sequence == 4 && x.Accepted)))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void FeatureControlRequest_KeepsDialogPendingDuringSelection()
        {
            var dialog = new NetworkDialog { Id = "dialog" };
            PrepareFeatureControlGame(new NetworkDialogState(dialog) { IsSelectingAnswer = true });
            var receive = FakeUtils.GetNetworkReceiverHandler<ClientFeatureControlRequested>(_networkServer);

            receive(42, new ClientFeatureControlRequested
            {
                Feature = nameof(NetworkPlayerControlledFeature.Dialogs), SessionId = "session", Sequence = 5
            });

            Assert.That(_multiplayerHost.Game.FeatureControllers[NetworkPlayerControlledFeature.Dialogs], Is.EqualTo(NetworkConstants.HostPlayerId));
            Assert.That(_multiplayerHost.Game.FeatureControlTransfers.TryGetValue(NetworkPlayerControlledFeature.Dialogs, out var pending), Is.True);
            Assert.That(pending.RequesterId, Is.EqualTo(42));
        }

        [Test]
        public void FeatureControlRequest_RejectsStaleSession()
        {
            PrepareFeatureControlGame();
            var receive = FakeUtils.GetNetworkReceiverHandler<ClientFeatureControlRequested>(_networkServer);

            receive(42, new ClientFeatureControlRequested
            {
                Feature = nameof(NetworkPlayerControlledFeature.VendorDeal), SessionId = "old-session", Sequence = 6
            });

            Assert.That(_multiplayerHost.Game.FeatureControllers[NetworkPlayerControlledFeature.VendorDeal], Is.EqualTo(NetworkConstants.HostPlayerId));
            A.CallTo(() => _networkServer.Broadcast(A<NotifyFeatureControlRequestResolved>.That.Matches(x =>
                x.RequesterId == 42 && x.Sequence == 6 && !x.Accepted && x.Reason == "invalid-request")))
                .MustHaveHappenedOnceExactly();
        }
    }
}
