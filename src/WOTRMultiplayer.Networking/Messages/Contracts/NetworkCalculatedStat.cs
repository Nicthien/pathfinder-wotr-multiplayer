using ProtoBuf;

namespace WOTRMultiplayer.Networking.Messages.Contracts
{
    [ProtoContract]
    public class NetworkCalculatedStat
    {
        [ProtoMember(1)] public int Type { get; set; }
        [ProtoMember(2)] public NetworkStatValues Values { get; set; }
    }
}
