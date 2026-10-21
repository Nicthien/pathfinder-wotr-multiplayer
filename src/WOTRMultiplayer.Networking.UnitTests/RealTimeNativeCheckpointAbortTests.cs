using System;
using NUnit.Framework;
using ProtoBuf;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Networking.UnitTests
{
    public class RealTimeNativeCheckpointAbortTests
    {
        private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        private static NotifyRealTimeNativeCheckpoint Source(bool archive)
            => archive ? RealTimeNativeCheckpointTransferTests.Manifest(new byte[42]) : RealTimeNativeCheckpointTransferTests.Prepare();

        [TestCase(false)][TestCase(true)]
        public void LostAbortAndReceiptAreRetriedWithOwnedIdenticalIdentity(bool archive)
        {
            var source = Source(archive);
            var abort = new RealTimeNativeCheckpointAbort(42, source, Now);
            var first = abort.GetDue(Now);
            source.SessionId = "mutated";
            Assert.That(RealTimeNativeCheckpointLimits.AbortSource(first), Is.Not.Null);
            Assert.That(abort.GetDue(Now.AddMilliseconds(4999)), Is.Null);
            first.CheckpointId = new string('2', 32);
            var retry = abort.GetDue(Now + RealTimeNativeCheckpointAbort.RetryDelay);
            Assert.That(retry.CheckpointId, Is.Not.EqualTo(first.CheckpointId));
            var ack = RealTimeNativeCheckpointTransferTests.Ack(retry, RealTimeNativeCheckpointAckStage.Aborted);
            Assert.That(abort.Acknowledge(42, Serializer.DeepClone(ack)), Is.True);
            Assert.That(abort.Acknowledge(42, ack), Is.True);
            Assert.That(abort.IsAcknowledged, Is.True);
            Assert.That(abort.GetDue(Now.AddDays(1)), Is.Null);
        }

        [TestCase("peer")][TestCase("session")][TestCase("save")][TestCase("epoch")][TestCase("checkpoint")]
        [TestCase("archive")][TestCase("number")][TestCase("stage")][TestCase("hash")][TestCase("reference")][TestCase("reason")]
        public void ReceiptIsBoundToPeerAndCheckpointAndCannotBeACommitReceipt(string mutation)
        {
            var abort = new RealTimeNativeCheckpointAbort(42, Source(true), Now);
            var ack = RealTimeNativeCheckpointTransferTests.Ack(abort.GetDue(Now), RealTimeNativeCheckpointAckStage.Aborted);
            switch (mutation)
            {
                case "session": ack.SessionId += "old"; break;
                case "save": ack.LoadedSaveSeed++; break;
                case "epoch": ack.Epoch++; break;
                case "checkpoint": ack.CheckpointId = new string('2', 32); break;
                case "archive": ack.ArchiveHash = new string('F', 64); break;
                case "number": ack.Number = 1; break;
                case "stage": ack.Stage = (int)RealTimeNativeCheckpointAckStage.ReferenceVerified; break;
                case "hash": ack.CanonicalStateHash = 1; break;
                case "reference": ack.ReferenceStateHash = new string('F', 64); break;
                case "reason": ack.Reason = "not-a-receipt"; break;
            }
            Assert.That(abort.Acknowledge(mutation == "peer" ? 99 : 42, ack), Is.False);
            Assert.That(abort.IsAcknowledged, Is.False);
        }

        [Test]
        public void RetransmissionExhaustionIsNotAnAcknowledgement()
        {
            var abort = new RealTimeNativeCheckpointAbort(42, Source(false), Now);
            for (var attempt = 0; attempt < RealTimeNativeCheckpointAbort.MaximumAttempts; attempt++)
                Assert.That(abort.GetDue(Now.AddSeconds(attempt * RealTimeNativeCheckpointAbort.RetryDelay.TotalSeconds)), Is.Not.Null);
            Assert.Throws<InvalidOperationException>(() => abort.GetDue(Now + RealTimeNativeCheckpointAbort.MaximumWait));
            Assert.That(abort.IsAcknowledged, Is.False);
        }

        [Test]
        public void AnUnsolicitedReceiptCannotConfirmAnAbortThatWasNeverSent()
        {
            var source = Source(false);
            var abort = new RealTimeNativeCheckpointAbort(42, source, Now);
            Assert.That(abort.Acknowledge(42, RealTimeNativeCheckpointTransferTests.Ack(source,
                RealTimeNativeCheckpointAckStage.Aborted)), Is.False);
            Assert.That(abort.IsAcknowledged, Is.False);
        }

        [TestCase("stage")][TestCase("bytes")][TestCase("archive")][TestCase("reference")][TestCase("size")]
        public void InvalidAbortHeaderDoesNotProduceASourceOrAllocation(string mutation)
        {
            var frame = new RealTimeNativeCheckpointAbort(42, Source(false), Now).GetDue(Now);
            switch (mutation)
            {
                case "stage": frame.Stage = (int)RealTimeNativeCheckpointStage.Load; break;
                case "bytes": frame.ArchiveBytes = 1; break;
                case "archive": frame.ArchiveHash = "invalid"; break;
                case "reference": frame.ReferenceStateBytes = new byte[8]; break;
                case "size": frame.ArchiveBytes = RealTimeNativeCheckpointLimits.MaximumArchiveBytes + 1; break;
            }
            Assert.That(RealTimeNativeCheckpointLimits.AbortSource(frame), Is.Null);
        }
    }
}
