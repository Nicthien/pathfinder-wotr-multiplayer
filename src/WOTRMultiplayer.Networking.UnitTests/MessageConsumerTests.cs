using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using WOTRMultiplayer.Networking.Abstractions;
using WOTRMultiplayer.Networking.Channels;
using WOTRMultiplayer.Networking.Consuming;

namespace WOTRMultiplayer.Networking.UnitTests
{
    [TestFixture]
    public class MessageConsumerTests
    {
        [Test]
        public void ObsoleteConsumerCannotConsumeReplacementGenerationMessages()
        {
            var consumer = new MessageConsumer(A.Fake<ILogger<MessageConsumer>>());
            var processed = 0;
            consumer.On<string>((_, _) => processed++, MessageHandlerPriority.Default);
            var oldCancellation = new CancellationTokenSource();
            oldCancellation.Cancel();
            var oldMessages = new BlockingCollection<NetworkMessageMetadata>();
            var replacementMessages = new BlockingCollection<NetworkMessageMetadata>();
            replacementMessages.Add(new NetworkMessageMetadata(NetworkChannelType.TCP, 42, "new-generation"));

            typeof(MessageConsumer).GetMethod("Consume", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(consumer, [1L, oldCancellation, oldMessages]);

            Assert.That(processed, Is.Zero);
            Assert.That(replacementMessages.Count, Is.EqualTo(1));
        }

        [Test]
        public void ResetDoesNotDisposeCancellationUsedByPreviousWorker()
        {
            var consumer = new MessageConsumer(A.Fake<ILogger<MessageConsumer>>());
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            consumer.On<string>((_, _) =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }, MessageHandlerPriority.Default);
            consumer.Enqueue(new NetworkMessageMetadata(NetworkChannelType.TCP, 42, "old-generation"));
            Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True);
            var previous = (CancellationTokenSource)typeof(MessageConsumer)
                .GetField("_cancellation", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(consumer);

            consumer.Reset();
            consumer.Enqueue(new NetworkMessageMetadata(NetworkChannelType.TCP, 42, "new-generation"));

            Assert.DoesNotThrow(() => _ = previous.Token);
            release.Set();
            consumer.Reset();
        }
    }
}
