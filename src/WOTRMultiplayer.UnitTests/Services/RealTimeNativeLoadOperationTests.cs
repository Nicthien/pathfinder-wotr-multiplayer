using System;
using System.Threading.Tasks;
using NUnit.Framework;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeNativeLoadOperationTests
    {
        private static RealTimeNativeWorldLoadResult Receipt() => new()
        { World = new object(), RepairObligation = new object() };

        [Test]
        public async Task DuplicateRequestCannotScheduleSecondNativeLoad()
        {
            var authorized = true;
            var operation = new RealTimeNativeLoadOperation(() => authorized);
            var pending = new TaskCompletionSource<RealTimeNativeWorldLoadResult>();
            var calls = 0;
            Task<RealTimeNativeWorldLoadResult> Load() { calls++; return pending.Task; }
            var first = operation.RunAsync(Load);
            Assert.That(operation.IsLoaded, Is.False);
            Assert.ThrowsAsync<InvalidOperationException>(() => operation.RunAsync(Load));
            pending.SetResult(Receipt()); await first;
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(operation.IsLoaded, Is.True);
            Assert.That(operation.Receipt.RepairObligation, Is.Not.Null);
            authorized = false;
            Assert.That(operation.IsLoaded, Is.False);
            Assert.ThrowsAsync<InvalidOperationException>(() => operation.RunAsync(Load));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RevocationBeforeOrDuringLoadCannotProduceReceipt(bool during)
        {
            var authorized = during;
            var operation = new RealTimeNativeLoadOperation(() => authorized);
            var calls = 0;
            Assert.ThrowsAsync<InvalidOperationException>(() => operation.RunAsync(() =>
            { calls++; authorized = false; return Task.FromResult(Receipt()); }));
            Assert.That(calls, Is.EqualTo(during ? 1 : 0));
            Assert.That(operation.Receipt, Is.Null);
            Assert.That(operation.IsLoaded, Is.False);
        }

        [Test]
        public void FailedNativeWorkerIsNotRetriedByDuplicatePacket()
        {
            var operation = new RealTimeNativeLoadOperation(() => true);
            Assert.ThrowsAsync<InvalidOperationException>(() => operation.RunAsync(() =>
                Task.FromException<RealTimeNativeWorldLoadResult>(new InvalidOperationException("synthetic-worker-failure"))));
            Assert.ThrowsAsync<InvalidOperationException>(() => operation.RunAsync(() => Task.FromResult(Receipt())));
            Assert.That(operation.IsLoaded, Is.False);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void IncompleteReceiptCannotAcknowledgeReconstruction(int missing)
        {
            var operation = new RealTimeNativeLoadOperation(() => true);
            var receipt = missing == 0 ? null : Receipt();
            if (missing == 1) receipt.World = null;
            if (missing == 2) receipt.RepairObligation = null;
            Assert.ThrowsAsync<InvalidOperationException>(() => operation.RunAsync(() => Task.FromResult(receipt)));
            Assert.That(operation.IsLoaded, Is.False);
        }
    }
}
