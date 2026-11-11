using System.Collections.Generic;
using System.Linq;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Area;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class MultiplayerHostTests
    {
        [Test]
        public void LocalRegroupPassesOnlyRequesterAndTargetCharactersToPlacement()
        {
            A.CallTo(() => _networkServer.IsActive).Returns(true);
            var host = new NetworkPlayer(-1) { IsHost = true };
            var other = new NetworkPlayer(7);
            var third = new NetworkPlayer(8);
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LoadedSaveSeed = 3, LocalPlayerId = -1,
                CurrentArea = new NetworkArea { Id = "area", Seed = 5 },
                Players = [host, other, third], Characters =
                [new NetworkCharacter { UnitId = "own", Owner = host },
                 new NetworkCharacter { UnitId = "target", Owner = other },
                 new NetworkCharacter { UnitId = "third", Owner = third }]
            };

            _multiplayerHost.RequestPartyRegroup(7);

            List<PartyRegroupPosition> ignored;
            A.CallTo(() => _combatInteractionService.TryRegroupParty(
                A<IReadOnlyCollection<string>>.That.Matches(ids => ids.SequenceEqual(new[] { "own" })),
                A<IReadOnlyCollection<string>>.That.Matches(ids => ids.SequenceEqual(new[] { "target" })),
                out ignored)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void LocalRegroupCannotTargetAnUnknownPlayer()
        {
            A.CallTo(() => _networkServer.IsActive).Returns(true);
            var host = new NetworkPlayer(-1) { IsHost = true };
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LocalPlayerId = -1, CurrentArea = new NetworkArea { Id = "area", Seed = 5 },
                Players = [host], Characters = [new NetworkCharacter { UnitId = "own", Owner = host }]
            };

            _multiplayerHost.RequestPartyRegroup(404);

            List<PartyRegroupPosition> ignored;
            A.CallTo(() => _combatInteractionService.TryRegroupParty(
                A<IReadOnlyCollection<string>>._, A<IReadOnlyCollection<string>>._, out ignored)).MustNotHaveHappened();
        }
    }
}
