using System;
using System.Threading.Tasks;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Leveling;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Networking.Messages.Lobby;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class MultiplayerHostTests
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task OwnershipCannotChangeUntilTheCharactersProgressionHasFinished(bool remote, bool pet)
        {
            var host = new NetworkPlayer(-1) { IsHost = true };
            var client = new NetworkPlayer(42);
            var character = new NetworkCharacter { UnitId = "hero", Owner = remote ? client : host };
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LocalPlayerId = -1, Players = [host, client], Characters = [character]
            };
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action action) => action());
            A.CallTo(() => _levelingInteractionService.IsBackgroundLevelingReady).Returns(true);
            A.CallTo(() => _levelingInteractionService.CompleteLeveling()).Invokes(() => _multiplayerHost.OnLevelingCompleted());
            if (pet) A.CallTo(() => _gameInteractionService.GetPetOwnerId("pet")).Returns("hero");
            var unitId = pet ? "pet" : "hero";
            NetworkLevelingIdentity identity = null;
            A.CallTo(() => _networkServer.Broadcast(A<NotifyLevelingStarted>._))
                .Invokes((object message) => identity = ((NotifyLevelingStarted)message).Leveling);
            if (remote)
                FakeUtils.GetNetworkReceiverHandler<ClientCharacterLevelingRequested>(_networkServer)(42,
                    new ClientCharacterLevelingRequested { UnitId = unitId, Type = "Leveling" });
            else _multiplayerHost.OnRequestLevelingUI(unitId, NetworkLevelingType.Leveling);
            Assert.That(identity, Is.Not.Null);
            var originalOwner = character.Owner;
            var target = remote ? host : client;
            _multiplayerHost.ChangeCharacterOwner(character, target);
            Assert.That(character.Owner, Is.SameAs(originalOwner), "The published progression identity must keep its owner until completion");
            A.CallTo(() => _networkServer.Broadcast(A<NotifyCharacterOwnerChanged>._)).MustNotHaveHappened();
            if (remote)
                FakeUtils.GetNetworkReceiverHandler<NotifyLevelingCompleted>(_networkServer)(42,
                    new NotifyLevelingCompleted { Leveling = identity });
            else _multiplayerHost.OnLevelingCompleted();
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (_multiplayerHost.IsSavingBlockedByCombatSync && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.That(_multiplayerHost.IsSavingBlockedByCombatSync, Is.False);
            _multiplayerHost.ChangeCharacterOwner(character, target);
            Assert.That(character.Owner, Is.SameAs(target));
            A.CallTo(() => _networkServer.Broadcast(A<NotifyCharacterOwnerChanged>._)).MustHaveHappenedOnceExactly();
        }
    }
}
