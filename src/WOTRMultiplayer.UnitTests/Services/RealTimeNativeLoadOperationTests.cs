using System;
using System.Threading.Tasks;
using Kingmaker.EntitySystem.Persistence;
using NUnit.Framework;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeNativeLoadOperationTests
    {
        [Test]
        public async Task FailedTaskCannotAcknowledgeAbortBeforeItsExactLeaseHasDrainedAndClosed()
        {
            var operation = new RealTimeNativeLoadOperation(() => true);
            var pending = new TaskCompletionSource<RealTimeNativeWorldLoadResult>();
            var task = operation.RunAsync(() => pending.Task);
            using var source = new RealTimeMemorySaver(); source.SaveJson("header", "{}");
            source.SaveJson("player", "{}"); source.SaveJson("party", "{}"); source.Seal();
            var info = new SaveInfo { Saver = source, FolderName = "RTwP-volatile-11111111111111111111111111111111.memory" };
            using var lease = RealTimeNativeLoadLease.Begin(new object(), info, () => true, (_, _) => { });
            operation.AttachLease(lease);
            Assert.That(operation.CanAcknowledgeAbort, Is.False);
            pending.SetException(new InvalidOperationException("synthetic-worker-fault"));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
            Assert.That(operation.CanAcknowledgeAbort, Is.False);
            lease.Fail("revoked"); lease.MarkWorkersDrained();
            Assert.That(operation.CanAcknowledgeAbort, Is.False);
            lease.Dispose();
            Assert.That(operation.CanAcknowledgeAbort, Is.True);
            Assert.That(operation.IsLoaded, Is.False);
            Assert.That(lease.IsDrainedAndCompleted, Is.False);
            await Task.CompletedTask;
        }

        [Test]
        public async Task DisposingWithoutAPositiveDrainObservationCannotConfirmAbort()
        {
            var operation = new RealTimeNativeLoadOperation(() => true);
            var pending = new TaskCompletionSource<RealTimeNativeWorldLoadResult>();
            var task = operation.RunAsync(() => pending.Task);
            using var source = new RealTimeMemorySaver(); source.SaveJson("header", "{}");
            source.SaveJson("player", "{}"); source.SaveJson("party", "{}"); source.Seal();
            var info = new SaveInfo { Saver = source, FolderName = "RTwP-volatile-11111111111111111111111111111111.memory" };
            using var lease = RealTimeNativeLoadLease.Begin(new object(), info, () => true, (_, _) => { });
            operation.AttachLease(lease);
            pending.SetResult(Receipt()); await task;
            lease.Dispose();
            Assert.That(operation.CanAcknowledgeAbort, Is.False);
        }

        [Test]
        public async Task DuplicateRequestCannotMakeAPendingFirstOperationLookSettled()
        {
            var authorized = true;
            var operation = new RealTimeNativeLoadOperation(() => authorized);
            var pending = new TaskCompletionSource<RealTimeNativeWorldLoadResult>();
            var task = operation.RunAsync(() => pending.Task);
            authorized = false;
            Assert.ThrowsAsync<InvalidOperationException>(() => operation.RunAsync(() => Task.FromResult(Receipt())));
            Assert.That(operation.CanAcknowledgeAbort, Is.False);
            pending.SetResult(Receipt());
            Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
            Assert.That(operation.CanAcknowledgeAbort, Is.True);
            Assert.That(operation.Receipt, Is.Null);
            await Task.CompletedTask;
        }

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
