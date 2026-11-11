using System.Collections.Generic;
using NUnit.Framework;
using ProtoBuf;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Networking.UnitTests
{
    public class PartyRegroupSerializationTests
    {
        [Test]
        public void RequestKeepsItsSessionAndDestination()
        {
            var copy = Serializer.DeepClone(new ClientPartyRegroupRequested
                { TargetPlayerId = 7, SessionId = "session", LoadSeed = 13, AreaSeed = 42 });
            Assert.That((copy.TargetPlayerId, copy.SessionId, copy.LoadSeed, copy.AreaSeed),
                Is.EqualTo((7L, "session", 13, 42)));
        }

        [Test]
        public void ResultPreservesAuthoritativePositions()
        {
            var copy = Serializer.DeepClone(new NotifyPartyRegrouped
            {
                SessionId = "session", LoadSeed = 13, AreaSeed = 42, RequesterId = 9, Success = true,
                IsAreaTransition = true, AreaPartId = "part", Sequence = 17,
                Positions = new List<PartyRegroupPosition> { new PartyRegroupPosition
                    { UnitId = "unit", X = 1.25f, Y = -2f, Z = 3f, Orientation = 90f, Scene = "scene" } }
            });
            Assert.That((copy.SessionId, copy.LoadSeed, copy.AreaSeed, copy.RequesterId, copy.Success),
                Is.EqualTo(("session", 13, 42, 9L, true)));
            Assert.That(copy.Positions, Has.Count.EqualTo(1));
            Assert.That((copy.IsAreaTransition, copy.AreaPartId, copy.Sequence), Is.EqualTo((true, "part", 17L)));
            Assert.That((copy.Positions[0].UnitId, copy.Positions[0].X, copy.Positions[0].Y,
                copy.Positions[0].Z, copy.Positions[0].Orientation, copy.Positions[0].Scene),
                Is.EqualTo(("unit", 1.25f, -2f, 3f, 90f, "scene")));
        }

        [TestCase(null)]
        [TestCase(0L)]
        [TestCase(90000000L)]
        public void AreaLifetimeSurvivesTransport(long? remaining)
        {
            var copy = Serializer.DeepClone(new WOTRMultiplayer.Networking.Messages.Contracts.NetworkAreaEffect
                { Id = "pit", RemainingDurationTicks = remaining });
            Assert.That(copy.RemainingDurationTicks, Is.EqualTo(remaining));
        }
    }
}
