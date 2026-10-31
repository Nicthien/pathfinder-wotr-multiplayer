using FakeItEasy;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using WOTRMultiplayer.Networking.Abstractions;
using WOTRMultiplayer.Networking.Abstractions.ExternalConnections;
using WOTRMultiplayer.Networking.Abstractions.TCP;
using WOTRMultiplayer.Networking.Channels;
using WOTRMultiplayer.Networking.Consuming;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Networking.UnitTests
{
    public class NetworkHostForwardingTests
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ProgressionsRequireActorValidationAndUnknownSendersNeverRelay(bool registered, bool leveling)
        {
            var server = A.Fake<INetworkServer>();
            var external = A.Fake<IExternalConnectionService>();
            var consumer = A.Fake<IMessageConsumer>();
            var connection = new NetworkHostConnection(A.Fake<ILogger<NetworkHostConnection>>(), consumer, server, external);
            if (registered) server.OnClientConnected(123);
            object message = leveling ? new NotifyLevelingNameChanged() : new NotifyUnitKilled();
            var metadata = new NetworkMessageMetadata(NetworkChannelType.TCP, 123, message);
            server.OnMessageReceived(metadata);

            if (registered && !leveling)
                A.CallTo(() => server.BroadcastExcept(123, message)).MustHaveHappenedOnceExactly();
            else
                A.CallTo(() => server.BroadcastExcept(A<long>._, message)).MustNotHaveHappened();
            if (registered)
            {
                A.CallTo(() => consumer.Enqueue(metadata)).MustHaveHappenedOnceExactly();
                Assert.That(metadata.PlayerId, Is.EqualTo(1));
            }
            else A.CallTo(() => consumer.Enqueue(A<NetworkMessageMetadata>._)).MustNotHaveHappened();
            System.GC.KeepAlive(connection);
        }
    }
}
