using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ProtoBuf;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Networking.UnitTests
{
    public class RealTimeNativeCheckpointTransferTests
    {
        internal static NotifyRealTimeNativeCheckpoint Prepare() => new()
        {
            SessionId = "session", LoadedSaveSeed = 17, Epoch = 3, CheckpointId = "11111111111111111111111111111111",
            BuildFingerprint = new string('A', 64), LastIncludedSequence = 7, CanonicalStateHash = 123,
            Stage = (int)RealTimeNativeCheckpointStage.Prepare
        };
        internal static NotifyRealTimeNativeCheckpoint Manifest(byte[] bytes)
        {
            var frame = Prepare(); frame.Stage = (int)RealTimeNativeCheckpointStage.Manifest;
            frame.ArchiveBytes = bytes.Length; frame.ArchiveHash = RealTimeNativeCheckpointLimits.Hash(bytes);
            frame.ChunkBytes = RealTimeNativeCheckpointLimits.ChunkBytes;
            frame.ChunkCount = (bytes.Length + frame.ChunkBytes - 1) / frame.ChunkBytes;
            return frame;
        }
        internal static byte[] Bytes(int size = 70003) => Enumerable.Range(0, size).Select(index => (byte)(index % 251)).ToArray();
        internal static ClientRealTimeNativeCheckpointAck Ack(NotifyRealTimeNativeCheckpoint manifest,
            RealTimeNativeCheckpointAckStage stage, int number = 0) => new()
        {
            SessionId = manifest.SessionId, LoadedSaveSeed = manifest.LoadedSaveSeed, Epoch = manifest.Epoch,
            CheckpointId = manifest.CheckpointId, ArchiveHash = manifest.ArchiveHash, Stage = (int)stage, Number = number
        };

        [Test]
        public void ReorderedDuplicatedAndCallerMutatedChunksProduceExactArchiveOnce()
        {
            var bytes = Bytes(); var manifest = Manifest(bytes);
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            using var receiver = new RealTimeNativeCheckpointReceiver(Prepare(), manifest);
            var chunks = sender.GetDueChunks(DateTime.UtcNow);
            foreach (var chunk in chunks.AsEnumerable().Reverse())
            {
                Assert.That(receiver.Accept(chunk), Is.True);
                Assert.That(receiver.Accept(Serializer.DeepClone(chunk)), Is.True);
            }
            chunks[0].Data[0] ^= 1;
            Assert.That(receiver.ReceivedChunks, Is.EqualTo(manifest.ChunkCount));
            Assert.That(receiver.IsComplete, Is.True);
            Assert.That(receiver.TakeCompleted(), Is.EqualTo(bytes));
            Assert.Throws<ObjectDisposedException>(() => receiver.TakeCompleted());
        }

        [Test]
        public void SenderOwnsSourceAndBothEndpointsOwnManifest()
        {
            var bytes = Bytes(); var expected = (byte[])bytes.Clone(); var prepare = Prepare(); var manifest = Manifest(bytes);
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            using var receiver = new RealTimeNativeCheckpointReceiver(prepare, manifest);
            bytes[0] ^= 1; manifest.ArchiveHash = new string('B', 64); prepare.Epoch++;
            foreach (var chunk in sender.GetDueChunks(DateTime.UtcNow)) Assert.That(receiver.Accept(chunk), Is.True);
            Assert.That(receiver.TakeCompleted(), Is.EqualTo(expected));
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(int.MaxValue)]
        [TestCase(138412033)]
        public void ForgedArchiveSizesAreRejectedBeforeAllocation(int bytes)
        {
            var frame = Manifest(new byte[] { 1 }); frame.ArchiveBytes = bytes;
            Assert.Throws<InvalidDataException>(() => new RealTimeNativeCheckpointReceiver(Prepare(), frame));
        }

        [TestCase("session")]
        [TestCase("save")]
        [TestCase("epoch")]
        [TestCase("id")]
        [TestCase("build")]
        [TestCase("sequence")]
        [TestCase("canonical")]
        [TestCase("chunk-size")]
        [TestCase("count")]
        [TestCase("hash")]
        public void ManifestMustMatchEveryPreparedIdentityAndLayout(string mutation)
        {
            var manifest = Manifest(Bytes());
            switch (mutation)
            {
                case "session": manifest.SessionId += "old"; break;
                case "save": manifest.LoadedSaveSeed++; break;
                case "epoch": manifest.Epoch++; break;
                case "id": manifest.CheckpointId = new string('2', 32); break;
                case "build": manifest.BuildFingerprint = new string('B', 64); break;
                case "sequence": manifest.LastIncludedSequence++; break;
                case "canonical": manifest.CanonicalStateHash++; break;
                case "chunk-size": manifest.ChunkBytes++; break;
                case "count": manifest.ChunkCount++; break;
                case "hash": manifest.ArchiveHash = "malformed"; break;
            }
            Assert.Throws<InvalidDataException>(() => new RealTimeNativeCheckpointReceiver(Prepare(), manifest));
        }

        [TestCase("session")]
        [TestCase("save")]
        [TestCase("epoch")]
        [TestCase("id")]
        [TestCase("hash")]
        [TestCase("number")]
        [TestCase("length")]
        [TestCase("content")]
        [TestCase("checksum")]
        public void InvalidOrOldChunkDoesNotAdvanceReceiver(string mutation)
        {
            var bytes = Bytes(); var manifest = Manifest(bytes);
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            using var receiver = new RealTimeNativeCheckpointReceiver(Prepare(), manifest);
            var chunk = sender.GetDueChunks(DateTime.UtcNow)[0];
            switch (mutation)
            {
                case "session": chunk.SessionId += "old"; break;
                case "save": chunk.LoadedSaveSeed++; break;
                case "epoch": chunk.Epoch--; break;
                case "id": chunk.CheckpointId = new string('2', 32); break;
                case "hash": chunk.ArchiveHash = new string('B', 64); break;
                case "number": chunk.Number = 0; break;
                case "length": chunk.Data = new byte[] { 1 }; break;
                case "content": chunk.Data[0] ^= 1; break;
                case "checksum": chunk.ChunkHash = "malformed"; break;
            }
            Assert.That(receiver.Accept(chunk), Is.False);
            Assert.That(receiver.ReceivedChunks, Is.Zero);
            Assert.That(receiver.IsComplete, Is.False);
            Assert.Throws<InvalidOperationException>(() => receiver.TakeCompleted());
        }

        [Test]
        public void ConflictingDuplicateCannotOverwriteAcceptedData()
        {
            var bytes = Bytes(); var manifest = Manifest(bytes);
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            using var receiver = new RealTimeNativeCheckpointReceiver(Prepare(), manifest);
            var chunks = sender.GetDueChunks(DateTime.UtcNow);
            Assert.That(receiver.Accept(chunks[0]), Is.True);
            var conflict = Serializer.DeepClone(chunks[0]); conflict.Data[0] ^= 1;
            conflict.ChunkHash = RealTimeNativeCheckpointLimits.Hash(conflict.Data);
            Assert.That(receiver.Accept(conflict), Is.False);
            foreach (var chunk in chunks.Skip(1)) Assert.That(receiver.Accept(chunk), Is.True);
            Assert.That(receiver.TakeCompleted(), Is.EqualTo(bytes));
        }

        [Test]
        public void CompleteArchiveHashIsCheckedIndependentlyOfChunkChecksums()
        {
            var bytes = Bytes(); var manifest = Manifest(bytes);
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            manifest.ArchiveHash = new string('B', 64);
            using var receiver = new RealTimeNativeCheckpointReceiver(Prepare(), manifest);
            var chunks = sender.GetDueChunks(DateTime.UtcNow);
            foreach (var chunk in chunks) chunk.ArchiveHash = manifest.ArchiveHash;
            foreach (var chunk in chunks.Take(chunks.Count - 1)) Assert.That(receiver.Accept(chunk), Is.True);
            Assert.Throws<InvalidDataException>(() => receiver.Accept(chunks.Last()));
            Assert.That(receiver.IsComplete, Is.False);
            Assert.Throws<ObjectDisposedException>(() => receiver.TakeCompleted());
        }

        [Test]
        public void WindowNeverQueuesMoreThanEightUnacknowledgedChunks()
        {
            var bytes = Bytes(RealTimeNativeCheckpointLimits.ChunkBytes * 20); var manifest = Manifest(bytes);
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            var now = DateTime.UtcNow;
            var first = sender.GetDueChunks(now);
            Assert.That(first.Count, Is.EqualTo(8));
            Assert.That(sender.GetDueChunks(now), Is.Empty);
            foreach (var chunk in first.Take(3)) Assert.That(sender.Acknowledge(42, Ack(manifest, RealTimeNativeCheckpointAckStage.Chunk, chunk.Number)), Is.True);
            Assert.That(sender.GetDueChunks(now).Select(chunk => chunk.Number), Is.EqualTo(new[] { 9, 10, 11 }));
            Assert.That(sender.GetDueChunks(now.AddSeconds(1)).Count, Is.EqualTo(8));
            Assert.That(sender.IsTransferred, Is.False);
        }

        [Test]
        public void LostChunkAndAcknowledgementAreRetransmittedAndReceivedReceiptIsNotMechanicalCommit()
        {
            var bytes = Bytes(); var manifest = Manifest(bytes); var now = DateTime.UtcNow;
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            using var receiver = new RealTimeNativeCheckpointReceiver(Prepare(), manifest);
            var first = sender.GetDueChunks(now);
            // Lose chunk 2 and the ACK for chunk 1.
            Assert.That(receiver.Accept(first[0]), Is.True);
            Assert.That(receiver.Accept(first[2]), Is.True);
            sender.Acknowledge(42, Ack(manifest, RealTimeNativeCheckpointAckStage.Chunk, first[2].Number));
            foreach (var chunk in sender.GetDueChunks(now.AddSeconds(1)))
            {
                Assert.That(receiver.Accept(chunk), Is.True);
                sender.Acknowledge(42, Ack(manifest, RealTimeNativeCheckpointAckStage.Chunk, chunk.Number));
            }
            Assert.That(receiver.IsComplete, Is.True);
            Assert.That(sender.IsTransferred, Is.False);
            Assert.That(sender.Acknowledge(42, Ack(manifest, RealTimeNativeCheckpointAckStage.Received)), Is.True);
            Assert.That(sender.IsTransferred, Is.True);
            Assert.That(sender.GetDueChunks(now.AddSeconds(2)), Is.Empty);
        }

        [Test]
        public void RetryExhaustionIsBoundedWithoutRealWaiting()
        {
            var bytes = Bytes(1); var manifest = Manifest(bytes);
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            var now = DateTime.UtcNow;
            for (var i = 0; i < RealTimeNativeCheckpointLimits.MaximumAttempts; i++)
                Assert.That(sender.GetDueChunks(now.AddSeconds(i)).Count, Is.EqualTo(1));
            Assert.Throws<TimeoutException>(() => sender.GetDueChunks(now.AddSeconds(RealTimeNativeCheckpointLimits.MaximumAttempts)));
        }

        [Test]
        public void UnsentForgedOldOrForeignAcknowledgementsCannotCompleteTransfer()
        {
            var bytes = Bytes(); var manifest = Manifest(bytes);
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            Assert.That(sender.Acknowledge(42, Ack(manifest, RealTimeNativeCheckpointAckStage.Received)), Is.False);
            Assert.That(sender.Acknowledge(42, Ack(manifest, RealTimeNativeCheckpointAckStage.Chunk, 1)), Is.False);
            sender.GetDueChunks(DateTime.UtcNow);
            Assert.That(sender.Acknowledge(99, Ack(manifest, RealTimeNativeCheckpointAckStage.Received)), Is.False);
            var old = Ack(manifest, RealTimeNativeCheckpointAckStage.Received); old.LoadedSaveSeed++;
            Assert.That(sender.Acknowledge(42, old), Is.False);
            var wrong = Ack(manifest, RealTimeNativeCheckpointAckStage.Received); wrong.ArchiveHash = new string('B', 64);
            Assert.That(sender.Acknowledge(42, wrong), Is.False);
            Assert.That(sender.IsTransferred, Is.False);
        }

        [Test]
        public void ProtobufPreservesPrepareManifestChunkAndReceiptIdentities()
        {
            var bytes = Bytes(1); var manifest = Serializer.DeepClone(Manifest(bytes));
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            var chunk = Serializer.DeepClone(sender.GetDueChunks(DateTime.UtcNow).Single());
            Assert.That(chunk.Number, Is.EqualTo(1)); Assert.That(chunk.Data, Is.EqualTo(bytes));
            using var receiver = new RealTimeNativeCheckpointReceiver(Serializer.DeepClone(Prepare()), manifest);
            Assert.That(receiver.Accept(chunk), Is.True);
            Assert.That(sender.Acknowledge(42, Serializer.DeepClone(Ack(manifest, RealTimeNativeCheckpointAckStage.Received))), Is.True);
        }

        [Test]
        public void DisposedEndpointsCannotContinueOrPublishData()
        {
            var bytes = Bytes(1); var manifest = Manifest(bytes);
            var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            var receiver = new RealTimeNativeCheckpointReceiver(Prepare(), manifest);
            var chunk = sender.GetDueChunks(DateTime.UtcNow).Single(); sender.Dispose(); receiver.Dispose();
            Assert.Throws<ObjectDisposedException>(() => sender.GetDueChunks(DateTime.UtcNow));
            Assert.Throws<ObjectDisposedException>(() => receiver.Accept(chunk));
        }
    }
}
