using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ProtoBuf;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Networking.UnitTests
{
    public class LevelingIdentitySerializationTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void ForcedOpeningFlagSurvivesSerialization(bool forced)
        {
            var copy = Serializer.DeepClone(new NotifyLevelingStarted
                { IsForced = forced, UnitId = "hero", Type = "MythicLeveling" });
            Assert.That(copy.IsForced, Is.EqualTo(forced));
        }

        public static IEnumerable<Type> Contracts => typeof(ILevelingMessage).Assembly.GetTypes()
            .Where(type => !type.IsInterface && typeof(ILevelingMessage).IsAssignableFrom(type));

        [TestCaseSource(nameof(Contracts))]
        public void EveryLevelingMessagePreservesIdentityThroughForwarding(Type contract)
        {
            var message = (ILevelingMessage)Activator.CreateInstance(contract);
            message.Leveling = new NetworkLevelingIdentity
            {
                Id = Guid.NewGuid().ToString("N"), SessionId = "session", LoadedSaveSeed = 17,
                UnitId = "hero", Type = "MythicLeveling", OwnerId = 42
            };
            using var stream = new MemoryStream();
            Serializer.NonGeneric.Serialize(stream, message);
            stream.Position = 0;
            var copy = (ILevelingMessage)Serializer.NonGeneric.Deserialize(contract, stream);
            Assert.That(copy.Leveling.Id, Is.EqualTo(message.Leveling.Id));
            Assert.That(copy.Leveling.SessionId, Is.EqualTo("session"));
            Assert.That(copy.Leveling.LoadedSaveSeed, Is.EqualTo(17));
            Assert.That(copy.Leveling.UnitId, Is.EqualTo("hero"));
            Assert.That(copy.Leveling.Type, Is.EqualTo("MythicLeveling"));
            Assert.That(copy.Leveling.OwnerId, Is.EqualTo(42));
        }
    }
}
