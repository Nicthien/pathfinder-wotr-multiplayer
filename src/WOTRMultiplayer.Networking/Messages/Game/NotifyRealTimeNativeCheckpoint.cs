using ProtoBuf;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    public enum RealTimeNativeCheckpointStage { Prepare = 1, Manifest = 2, Abort = 3, Load = 4, Verify = 5, Commit = 6 }
    // Received means verified bytes, NEVER reconstructed mechanics or permission to resume/save.
    // Loaded is a reconstruction receipt, still NOT a verified Commit.
    // Aborted confirms revoked work has settled/drained; never a Commit or resume.
    public enum RealTimeNativeCheckpointAckStage { Prepared = 1, Chunk = 2, Received = 3, Failed = 4, Loaded = 5, ReferenceVerified = 6, Aborted = 7, Committed = 8 }

    [ProtoContract, MessageType((int)MessageTypes.Game.NotifyRealTimeNativeCheckpoint)]
    public class NotifyRealTimeNativeCheckpoint
    {
        [ProtoMember(1)] public string SessionId { get; set; }
        [ProtoMember(2)] public int LoadedSaveSeed { get; set; }
        [ProtoMember(3)] public long Epoch { get; set; }
        [ProtoMember(4)] public string CheckpointId { get; set; }
        [ProtoMember(5)] public string BuildFingerprint { get; set; }
        [ProtoMember(6)] public long LastIncludedSequence { get; set; }
        [ProtoMember(7)] public ulong CanonicalStateHash { get; set; }
        [ProtoMember(8)] public int Stage { get; set; }
        [ProtoMember(9)] public string ArchiveHash { get; set; }
        [ProtoMember(10)] public int ArchiveBytes { get; set; }
        [ProtoMember(11)] public int ChunkBytes { get; set; }
        [ProtoMember(12)] public int ChunkCount { get; set; }
        [ProtoMember(13)] public byte[] ReferenceStateBytes { get; set; }
        [ProtoMember(14)] public string ReferenceStateHash { get; set; }
    }

    [ProtoContract, MessageType((int)MessageTypes.Game.NotifyRealTimeNativeCheckpointChunk)]
    public class NotifyRealTimeNativeCheckpointChunk
    {
        [ProtoMember(1)] public string SessionId { get; set; }
        [ProtoMember(2)] public int LoadedSaveSeed { get; set; }
        [ProtoMember(3)] public long Epoch { get; set; }
        [ProtoMember(4)] public string CheckpointId { get; set; }
        [ProtoMember(5)] public string ArchiveHash { get; set; }
        [ProtoMember(6)] public int Number { get; set; } // 1-based; 0 is never a chunk.
        [ProtoMember(7)] public byte[] Data { get; set; }
        [ProtoMember(8)] public string ChunkHash { get; set; }
    }

    [ProtoContract, MessageType((int)MessageTypes.Game.ClientRealTimeNativeCheckpointAck)]
    public class ClientRealTimeNativeCheckpointAck
    {
        [ProtoMember(1)] public string SessionId { get; set; }
        [ProtoMember(2)] public int LoadedSaveSeed { get; set; }
        [ProtoMember(3)] public long Epoch { get; set; }
        [ProtoMember(4)] public string CheckpointId { get; set; }
        [ProtoMember(5)] public string ArchiveHash { get; set; }
        [ProtoMember(6)] public int Stage { get; set; }
        [ProtoMember(7)] public int Number { get; set; }
        [ProtoMember(8)] public string Reason { get; set; }
        [ProtoMember(9)] public ulong CanonicalStateHash { get; set; }
        [ProtoMember(10)] public string ReferenceStateHash { get; set; }
    }
}
