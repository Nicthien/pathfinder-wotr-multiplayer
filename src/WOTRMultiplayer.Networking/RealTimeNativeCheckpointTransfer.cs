using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Networking
{
    public static class RealTimeNativeCheckpointLimits
    {
        public const int MaximumArchiveBytes = 132 * 1024 * 1024;
        public const int ChunkBytes = 32 * 1024;
        public const int Window = 8;
        public const int MaximumAttempts = 12;
        public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

        public static bool IsHash(string value) => value?.Length == 64
            && value.All(character => character >= '0' && character <= '9' || character >= 'A' && character <= 'F');

        public static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }

        public static bool HasIdentity(NotifyRealTimeNativeCheckpoint frame) => frame != null
            && !string.IsNullOrWhiteSpace(frame.SessionId) && frame.SessionId.Length <= 128
            && Guid.TryParseExact(frame.CheckpointId, "N", out _) && IsHash(frame.BuildFingerprint)
            && frame.Epoch > 0 && frame.LastIncludedSequence >= 0;

        public static bool SameIdentity(NotifyRealTimeNativeCheckpoint left, NotifyRealTimeNativeCheckpoint right)
            => HasIdentity(left) && HasIdentity(right) && left.SessionId == right.SessionId
                && left.LoadedSaveSeed == right.LoadedSaveSeed && left.Epoch == right.Epoch
                && left.CheckpointId == right.CheckpointId && left.BuildFingerprint == right.BuildFingerprint
                && left.LastIncludedSequence == right.LastIncludedSequence && left.CanonicalStateHash == right.CanonicalStateHash;

        public static bool IsPrepare(NotifyRealTimeNativeCheckpoint frame) => HasIdentity(frame)
            && frame.Stage == (int)RealTimeNativeCheckpointStage.Prepare && frame.ArchiveBytes == 0
            && frame.ChunkBytes == 0 && frame.ChunkCount == 0 && string.IsNullOrEmpty(frame.ArchiveHash);

        public static bool IsManifest(NotifyRealTimeNativeCheckpoint frame) => HasIdentity(frame)
            && frame.Stage == (int)RealTimeNativeCheckpointStage.Manifest && IsHash(frame.ArchiveHash)
            && frame.ArchiveBytes > 0 && frame.ArchiveBytes <= MaximumArchiveBytes
            && frame.ChunkBytes == ChunkBytes && frame.ChunkCount == (frame.ArchiveBytes + ChunkBytes - 1) / ChunkBytes;

        public static NotifyRealTimeNativeCheckpoint AsManifest(NotifyRealTimeNativeCheckpoint frame)
        {
            if (frame == null || frame.Stage != (int)RealTimeNativeCheckpointStage.Load) return null;
            var manifest = Copy(frame);
            manifest.Stage = (int)RealTimeNativeCheckpointStage.Manifest;
            return IsManifest(manifest) ? manifest : null;
        }

        public static NotifyRealTimeNativeCheckpoint Copy(NotifyRealTimeNativeCheckpoint frame) => new()
        {
            SessionId = frame.SessionId, LoadedSaveSeed = frame.LoadedSaveSeed, Epoch = frame.Epoch,
            CheckpointId = frame.CheckpointId, BuildFingerprint = frame.BuildFingerprint,
            LastIncludedSequence = frame.LastIncludedSequence, CanonicalStateHash = frame.CanonicalStateHash,
            Stage = frame.Stage, ArchiveHash = frame.ArchiveHash, ArchiveBytes = frame.ArchiveBytes,
            ChunkBytes = frame.ChunkBytes, ChunkCount = frame.ChunkCount
        };

        public static bool Matches(NotifyRealTimeNativeCheckpoint frame, ClientRealTimeNativeCheckpointAck ack)
            => frame != null && ack != null && ack.SessionId == frame.SessionId && ack.LoadedSaveSeed == frame.LoadedSaveSeed
                && ack.Epoch == frame.Epoch && ack.CheckpointId == frame.CheckpointId && ack.ArchiveHash == frame.ArchiveHash;

        public static TimeSpan Deadline(int bytes) => TimeSpan.FromSeconds(30 + Math.Min(150, Math.Max(0, bytes) / (1024 * 1024)));
    }

    // One immutable manifest per prepared barrier. No native JSON decoding here.
    public sealed class RealTimeNativeCheckpointReceiver : IDisposable
    {
        private readonly NotifyRealTimeNativeCheckpoint _manifest;
        private readonly bool[] _received;
        private byte[] _archive;
        private int _count;
        public bool IsComplete { get; private set; }
        public int ReceivedChunks => _count;

        public RealTimeNativeCheckpointReceiver(NotifyRealTimeNativeCheckpoint prepare, NotifyRealTimeNativeCheckpoint manifest)
        {
            prepare = prepare == null ? null : RealTimeNativeCheckpointLimits.Copy(prepare);
            manifest = manifest == null ? null : RealTimeNativeCheckpointLimits.Copy(manifest);
            if (!RealTimeNativeCheckpointLimits.IsPrepare(prepare) || !RealTimeNativeCheckpointLimits.IsManifest(manifest)
                || !RealTimeNativeCheckpointLimits.SameIdentity(prepare, manifest))
                throw new InvalidDataException("native-transfer-manifest-not-bound-to-prepare");
            _manifest = manifest;
            _received = new bool[manifest.ChunkCount];
            _archive = new byte[manifest.ArchiveBytes]; // Bounded, once, only after a matching Prepare.
        }

        public bool MatchesManifest(NotifyRealTimeNativeCheckpoint frame) => RealTimeNativeCheckpointLimits.IsManifest(frame)
            && RealTimeNativeCheckpointLimits.SameIdentity(_manifest, frame) && _manifest.ArchiveHash == frame.ArchiveHash
            && _manifest.ArchiveBytes == frame.ArchiveBytes && _manifest.ChunkCount == frame.ChunkCount;

        public bool Accept(NotifyRealTimeNativeCheckpointChunk chunk)
        {
            if (_archive == null) throw new ObjectDisposedException(nameof(RealTimeNativeCheckpointReceiver));
            if (chunk == null || chunk.SessionId != _manifest.SessionId || chunk.LoadedSaveSeed != _manifest.LoadedSaveSeed
                || chunk.Epoch != _manifest.Epoch || chunk.CheckpointId != _manifest.CheckpointId
                || chunk.ArchiveHash != _manifest.ArchiveHash || chunk.Number < 1 || chunk.Number > _received.Length
                || chunk.Data == null || !RealTimeNativeCheckpointLimits.IsHash(chunk.ChunkHash)) return false;
            var index = chunk.Number - 1;
            var offset = index * RealTimeNativeCheckpointLimits.ChunkBytes;
            var length = Math.Min(RealTimeNativeCheckpointLimits.ChunkBytes, _archive.Length - offset);
            if (chunk.Data.Length != length) return false;
            var owned = (byte[])chunk.Data.Clone();
            if (RealTimeNativeCheckpointLimits.Hash(owned) != chunk.ChunkHash) return false;
            if (_received[index])
            {
                for (var i = 0; i < length; i++)
                    if (_archive[offset + i] != owned[i]) return false;
                return true;
            }
            Buffer.BlockCopy(owned, 0, _archive, offset, length);
            _received[index] = true; _count++;
            if (_count == _received.Length)
            {
                if (RealTimeNativeCheckpointLimits.Hash(_archive) != _manifest.ArchiveHash)
                { Dispose(); throw new InvalidDataException("native-transfer-complete-hash-mismatch"); }
                IsComplete = true;
            }
            return true;
        }

        public byte[] TakeCompleted()
        {
            if (_archive == null) throw new ObjectDisposedException(nameof(RealTimeNativeCheckpointReceiver));
            if (!IsComplete) throw new InvalidOperationException("native-transfer-incomplete");
            var bytes = _archive; _archive = null; return bytes;
        }
        public void Dispose() { _archive = null; IsComplete = false; }
    }

    public sealed class RealTimeNativeCheckpointSender : IDisposable
    {
        private readonly NotifyRealTimeNativeCheckpoint _manifest;
        private readonly long _peer;
        private readonly bool[] _acknowledged;
        private readonly int[] _attempts;
        private readonly DateTime[] _nextSend;
        private byte[] _archive;
        public bool IsTransferred { get; private set; }

        public RealTimeNativeCheckpointSender(long peer, NotifyRealTimeNativeCheckpoint manifest, byte[] archive)
        {
            manifest = manifest == null ? null : RealTimeNativeCheckpointLimits.Copy(manifest);
            if (!RealTimeNativeCheckpointLimits.IsManifest(manifest) || archive == null || archive.Length != manifest.ArchiveBytes)
                throw new InvalidDataException("native-transfer-invalid-source");
            var owned = (byte[])archive.Clone();
            if (RealTimeNativeCheckpointLimits.Hash(owned) != manifest.ArchiveHash)
                throw new InvalidDataException("native-transfer-invalid-source-hash");
            _manifest = manifest; _peer = peer; _archive = owned;
            _acknowledged = new bool[manifest.ChunkCount]; _attempts = new int[manifest.ChunkCount];
            _nextSend = new DateTime[manifest.ChunkCount];
        }

        public List<NotifyRealTimeNativeCheckpointChunk> GetDueChunks(DateTime now)
        {
            if (_archive == null) throw new ObjectDisposedException(nameof(RealTimeNativeCheckpointSender));
            var result = new List<NotifyRealTimeNativeCheckpointChunk>();
            if (IsTransferred) return result;
            var outstanding = _attempts.Where((attempt, index) => attempt > 0 && !_acknowledged[index]).Count();
            for (var index = 0; index < _acknowledged.Length && result.Count < RealTimeNativeCheckpointLimits.Window; index++)
            {
                if (_acknowledged[index] || now < _nextSend[index]) continue;
                if (_attempts[index] == 0 && outstanding >= RealTimeNativeCheckpointLimits.Window) continue;
                if (_attempts[index] >= RealTimeNativeCheckpointLimits.MaximumAttempts)
                    throw new TimeoutException("native-transfer-chunk-retries-exhausted");
                if (_attempts[index]++ == 0) outstanding++;
                _nextSend[index] = now + RealTimeNativeCheckpointLimits.RetryDelay;
                var offset = index * RealTimeNativeCheckpointLimits.ChunkBytes;
                var bytes = new byte[Math.Min(RealTimeNativeCheckpointLimits.ChunkBytes, _archive.Length - offset)];
                Buffer.BlockCopy(_archive, offset, bytes, 0, bytes.Length);
                result.Add(new NotifyRealTimeNativeCheckpointChunk
                {
                    SessionId = _manifest.SessionId, LoadedSaveSeed = _manifest.LoadedSaveSeed, Epoch = _manifest.Epoch,
                    CheckpointId = _manifest.CheckpointId, ArchiveHash = _manifest.ArchiveHash,
                    Number = index + 1, Data = bytes, ChunkHash = RealTimeNativeCheckpointLimits.Hash(bytes)
                });
            }
            return result;
        }

        public bool Acknowledge(long peer, ClientRealTimeNativeCheckpointAck ack)
        {
            if (_archive == null) throw new ObjectDisposedException(nameof(RealTimeNativeCheckpointSender));
            if (peer != _peer || !RealTimeNativeCheckpointLimits.Matches(_manifest, ack)) return false;
            if (ack.Stage == (int)RealTimeNativeCheckpointAckStage.Chunk)
            {
                if (ack.Number < 1 || ack.Number > _acknowledged.Length || _attempts[ack.Number - 1] == 0) return false;
                _acknowledged[ack.Number - 1] = true; return true;
            }
            if (ack.Stage != (int)RealTimeNativeCheckpointAckStage.Received || ack.Number != 0
                || _attempts.Any(attempt => attempt == 0)) return false;
            IsTransferred = true; return true;
        }

        public void Dispose() { _archive = null; }
    }
}
