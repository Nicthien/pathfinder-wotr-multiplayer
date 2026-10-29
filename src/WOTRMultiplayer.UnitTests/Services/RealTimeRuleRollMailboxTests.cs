using System.Threading.Tasks;
using NUnit.Framework;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeRuleRollMailboxTests
    {
        [Test]
        public void HostSequenceAndEarlyClientDelivery_MatchInOrder()
        {
            var host = new RealTimeRuleRollMailbox();
            var client = new RealTimeRuleRollMailbox();
            var first = host.Publish(4, "attack:d20");
            var second = host.Publish(4, "attack:d20");
            Assert.That(client.Receive(4, "attack:d20", second, 17), Is.True);
            Assert.That(client.Receive(4, "attack:d20", first, 12), Is.True);

            Assert.That(client.TryConsume(4, "attack:d20", 0, out var firstSequence, out var firstResult), Is.True);
            Assert.That(client.TryConsume(4, "attack:d20", 0, out var secondSequence, out var secondResult), Is.True);
            Assert.That((firstSequence, firstResult, secondSequence, secondResult), Is.EqualTo((1L, 12, 2L, 17)));
        }

        [Test]
        public async Task DelayedHostResult_WakesWaitingClient()
        {
            var client = new RealTimeRuleRollMailbox();
            var wait = Task.Run(() =>
            {
                var received = client.TryConsume(5, "damage:2d6", 1000, out var sequence, out var result);
                return (received, sequence, result);
            });
            await Task.Delay(20);
            Assert.That(client.Receive(5, "damage:2d6", 1, 8), Is.True);

            Assert.That(await wait, Is.EqualTo((true, 1L, 8)));
        }

        [Test]
        public void TimeoutRejectsLateResult_AndNextSequenceCanStillArrive()
        {
            var client = new RealTimeRuleRollMailbox();
            Assert.That(client.TryConsume(6, "save:d20", 0, out var missed, out _), Is.False);
            Assert.That(missed, Is.EqualTo(1));
            Assert.That(client.Receive(6, "save:d20", 1, 11), Is.False);
            Assert.That(client.Receive(6, "save:d20", 2, 18), Is.True);
            Assert.That(client.TryConsume(6, "save:d20", 0, out var next, out var result), Is.True);
            Assert.That((next, result), Is.EqualTo((2L, 18)));
        }

        [Test]
        public void NewEpochClearsOldResults_AndRejectsStaleDelivery()
        {
            var client = new RealTimeRuleRollMailbox();
            Assert.That(client.Receive(7, "attack:d20", 1, 10), Is.True);
            Assert.That(client.Receive(8, "attack:d20", 1, 15), Is.True);
            Assert.That(client.Receive(7, "attack:d20", 2, 20), Is.False);
            Assert.That(client.TryConsume(8, "attack:d20", 0, out var sequence, out var result), Is.True);
            Assert.That((sequence, result), Is.EqualTo((1L, 15)));
        }

        [Test]
        public void ConflictingDuplicateIsRejected()
        {
            var client = new RealTimeRuleRollMailbox();
            Assert.That(client.Receive(9, "attack:d20", 1, 10), Is.True);
            Assert.That(client.Receive(9, "attack:d20", 1, 11), Is.False);
        }

        [Test]
        public void ResetAllowsNewCombatToReuseEpochAndSequence()
        {
            var mailbox = new RealTimeRuleRollMailbox();
            Assert.That(mailbox.Publish(3, "attack:d20"), Is.EqualTo(1));
            Assert.That(mailbox.Publish(3, "attack:d20"), Is.EqualTo(2));

            mailbox.Reset();

            Assert.That(mailbox.Publish(3, "attack:d20"), Is.EqualTo(1));
        }
    }
}
