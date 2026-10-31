using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Leveling;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class MultiplayerHostTests
    {
        [Test]
        public void OwnedChoicesCarryTheIdentityPublishedAtStart()
        {
            var host = new NetworkPlayer(-1) { IsHost = true };
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LoadedSaveSeed = 7, LocalPlayerId = -1, Players = [host],
                Characters = [new NetworkCharacter { UnitId = "hero", Owner = host }]
            };
            NetworkLevelingIdentity published = null;
            A.CallTo(() => _networkServer.Broadcast(A<NotifyLevelingStarted>._))
                .Invokes((object message) => published = ((NotifyLevelingStarted)message).Leveling);

            Assert.That(_multiplayerHost.OnRequestLevelingUI("hero", NetworkLevelingType.Leveling), Is.True);
            Assert.That(published, Is.Not.Null);
            _multiplayerHost.OnLevelingNameChanged("Hero");

            A.CallTo(() => _networkServer.Broadcast(A<NotifyLevelingNameChanged>.That.Matches(message =>
                message.Leveling != null && message.Leveling.Id == published.Id
                && message.Leveling.UnitId == "hero" && message.Leveling.SessionId == "session"
                && message.Leveling.LoadedSaveSeed == 7 && message.Leveling.OwnerId == -1)))
                .MustHaveHappenedOnceExactly();
        }
    }
}
