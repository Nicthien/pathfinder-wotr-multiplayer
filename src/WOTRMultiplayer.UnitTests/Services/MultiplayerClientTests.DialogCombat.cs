using System;
using System.Threading.Tasks;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Area;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Dialogs;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class MultiplayerClientTests
    {
        [Test]
        public void CombatStartedClosesTheActiveDialog()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 1,
                DialogState = new NetworkDialogState(new NetworkDialog())
            };

            _multiplayerClient.CombatStarted();

            A.CallTo(() => _dialogInteractionService.StopDialog()).MustHaveHappenedOnceExactly();
            Assert.That(_multiplayerClient.Game.DialogState, Is.Null);
            Assert.That(_multiplayerClient.Game.Combat, Is.Not.Null);
        }
        [TestCase(-1L, -1L, true)]
        [TestCase(-1L, 2L, false)]
        [TestCase(2L, -1L, false)]
        public void OnlyHostCanCloseClientDialog(long sender, long player, bool closes)
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 1,
                DialogState = new NetworkDialogState(new NetworkDialog())
            };
            FakeUtils.GetNetworkReceiverHandler<NotifyGameModeTypeEnded>(_networkClient)(sender,
                new NotifyGameModeTypeEnded { PlayerId = player, Type = Kingmaker.GameModes.GameModeType.Dialog.Name });
            A.CallTo(() => _dialogInteractionService.StopDialog()).MustHaveHappened(closes ? 1 : 0, Times.Exactly);
            Assert.That(_multiplayerClient.Game.DialogState == null, Is.EqualTo(closes));
        }

        [Test]
        public async Task HostDialogCanRecoverAnExpiredUnconfirmedCombatStart()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LoadedSaveSeed = 7, CurrentArea = new NetworkArea { Seed = 5 },
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Initiating }
            };
            _multiplayerClient.FailCombatStartup("initialization-timeout:Initiating");
            A.CallTo(() => _dialogInteractionService.StartDialogAsync(A<NetworkDialog>._)).Returns(Task.FromResult(true));
            FakeUtils.GetNetworkReceiverHandler<NotifyDialogStarted>(_networkClient)(-1, new NotifyDialogStarted
            {
                HostOutsideCombat = true, SessionId = "session", LoadedSaveSeed = 7, AreaSeed = 5,
                Dialog = new WOTRMultiplayer.Networking.Messages.Contracts.NetworkDialog { Id = "dialog", IsScripted = true }
            });
            var until = DateTime.UtcNow.AddSeconds(2);
            while (_multiplayerClient.HasCombatStartupFailure && DateTime.UtcNow < until) await Task.Delay(10);
            Assert.That(_multiplayerClient.HasCombatStartupFailure, Is.False);
            Assert.That(_multiplayerClient.Game.Combat, Is.Null);
        }

        [TestCase("session", 7, 5, true)]
        [TestCase("old", 7, 5, false)]
        [TestCase("session", 6, 5, false)]
        [TestCase("session", 7, 4, false)]
        public async Task HostDialogCancelsOnlyCurrentClientCombatStartup(string id, int save, int area, bool accepted)
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LoadedSaveSeed = 7, CurrentArea = new NetworkArea { Seed = 5 },
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Initiating }
            };
            A.CallTo(() => _dialogInteractionService.StartDialogAsync(A<NetworkDialog>._)).Returns(Task.FromResult(true));
            FakeUtils.GetNetworkReceiverHandler<NotifyDialogStarted>(_networkClient)(-1, new NotifyDialogStarted
            {
                HostOutsideCombat = true, SessionId = id, LoadedSaveSeed = save, AreaSeed = area,
                Dialog = new WOTRMultiplayer.Networking.Messages.Contracts.NetworkDialog { Id = "dialog", IsScripted = true }
            });
            if (accepted)
            {
                var until = DateTime.UtcNow.AddSeconds(2);
                while (_multiplayerClient.Game.Combat != null && DateTime.UtcNow < until) await Task.Delay(10);
                A.CallTo(() => _combatInteractionService.ForceResetCombat()).MustHaveHappenedOnceExactly();
                Assert.That(_multiplayerClient.Game.Combat, Is.Null);
            }
            else
            {
                await Task.Delay(30);
                A.CallTo(() => _combatInteractionService.ForceResetCombat()).MustNotHaveHappened();
                Assert.That(_multiplayerClient.Game.Combat, Is.Not.Null);
            }
        }
    }
}
