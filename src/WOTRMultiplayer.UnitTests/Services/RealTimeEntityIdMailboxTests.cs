using System.Threading.Tasks;
using NUnit.Framework;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeEntityIdMailboxTests
    {
        [Test]
        public void OutOfOrderDelivery_MatchesHostIdsByKeyAndSequence()
        {
            var host = new RealTimeEntityIdMailbox();
            var client = new RealTimeEntityIdMailbox();
            Assert.That(host.Publish(4, "area:cloud"), Is.EqualTo(1));
            Assert.That(host.Publish(4, "area:cloud"), Is.EqualTo(2));
            Assert.That(client.Receive(4, "area:cloud", 2, "second-id"), Is.True);
            Assert.That(client.Receive(4, "area:cloud", 1, "first-id"), Is.True);

            Assert.That(client.TryConsume(4, "area:cloud", 0, out var first, out var firstId), Is.True);
            Assert.That(client.TryConsume(4, "area:cloud", 0, out var second, out var secondId), Is.True);
            Assert.That((first, firstId, second, secondId), Is.EqualTo((1L, "first-id", 2L, "second-id")));
        }

        [Test]
        public async Task DelayedHostId_WakesWaitingClient()
        {
            var client = new RealTimeEntityIdMailbox();
            var pending = Task.Run(() =>
            {
                var received = client.TryConsume(5, "area:fire", 1000, out var sequence, out var id);
                return (received, sequence, id);
            });
            await Task.Delay(20);
            Assert.That(client.Receive(5, "area:fire", 1, "host-id"), Is.True);
            Assert.That(await pending, Is.EqualTo((true, 1L, "host-id")));
        }

        [Test]
        public void TimeoutAndEpochChange_RejectLateIds()
        {
            var client = new RealTimeEntityIdMailbox();
            Assert.That(client.TryConsume(6, "area:cloud", 0, out var missed, out _), Is.False);
            Assert.That(missed, Is.EqualTo(1));
            Assert.That(client.Receive(6, "area:cloud", 1, "late"), Is.False);
            Assert.That(client.Receive(7, "area:cloud", 1, "new"), Is.True);
            Assert.That(client.Receive(6, "area:cloud", 2, "old"), Is.False);
            Assert.That(client.TryConsume(7, "area:cloud", 0, out var sequence, out var id), Is.True);
            Assert.That((sequence, id), Is.EqualTo((1L, "new")));
        }

        [Test]
        public void ConflictingDuplicateAndInvalidId_AreRejected()
        {
            var client = new RealTimeEntityIdMailbox();
            Assert.That(client.Receive(8, "area:cloud", 1, "id-a"), Is.True);
            Assert.That(client.Receive(8, "area:cloud", 1, "id-b"), Is.False);
            Assert.That(client.Receive(8, "area:cloud", 2, null), Is.False);
        }

        [Test]
        public void ResetAllowsNewSessionToReuseEpoch()
        {
            var mailbox = new RealTimeEntityIdMailbox();
            Assert.That(mailbox.Publish(3, "area:cloud"), Is.EqualTo(1));
            mailbox.Reset();
            Assert.That(mailbox.Publish(3, "area:cloud"), Is.EqualTo(1));
        }
    }
}
