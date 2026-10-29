using System.Collections.Generic;
using ProtoBuf;

namespace WOTRMultiplayer.Networking.Messages.Contracts
{
    [ProtoContract]
    public sealed class NetworkNativeTopology
    {
        [ProtoMember(1)] public List<NetworkNativeEntityTopology> Entities { get; set; } = new();
    }
    [ProtoContract]
    public sealed class NetworkNativeEntityTopology
    {
        [ProtoMember(1)] public string Id { get; set; }
        [ProtoMember(2)] public string Kind { get; set; }
        [ProtoMember(3)] public List<string> Parts { get; set; } = new();
        [ProtoMember(4)] public List<NetworkNativeFactTopology> Facts { get; set; } = new();
    }
    [ProtoContract]
    public sealed class NetworkNativeFactTopology
    {
        [ProtoMember(1)] public string Id { get; set; }
        [ProtoMember(2)] public string RuntimeType { get; set; }
        [ProtoMember(3)] public string BlueprintId { get; set; }
        [ProtoMember(4)] public List<NetworkNativeComponentTopology> Components { get; set; } = new();
    }
    [ProtoContract]
    public sealed class NetworkNativeComponentTopology
    {
        [ProtoMember(1)] public string RuntimeType { get; set; }
        [ProtoMember(2)] public string SourceName { get; set; } = "";
    }
}
