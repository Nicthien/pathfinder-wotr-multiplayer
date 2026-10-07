using System.Collections.Generic;
using ProtoBuf;

namespace WOTRMultiplayer.Networking.Messages.Contracts
{
    // Evidence of accepted intentions, not executable commands or active native actions.
    [ProtoContract]
    public sealed class NetworkAcceptedOrderReference
    {
        [ProtoMember(1)] public string SessionId { get; set; }
        [ProtoMember(2)] public int LoadedSaveSeed { get; set; }
        [ProtoMember(3)] public long Epoch { get; set; }
        [ProtoMember(4)] public long LastIncludedSequence { get; set; }
        [ProtoMember(5)] public List<NetworkAcceptedOrderEvidence> Orders { get; set; } = [];
    }

    [ProtoContract]
    public sealed class NetworkAcceptedOrderEvidence
    {
        [ProtoMember(1)] public long Sequence { get; set; }
        [ProtoMember(2)] public long PlayerId { get; set; }
        [ProtoMember(3)] public string UnitId { get; set; }
        [ProtoMember(4)] public int Kind { get; set; }
        [ProtoMember(5)] public string PayloadHash { get; set; }
        [ProtoMember(6)] public string CommandId { get; set; }
    }
}
