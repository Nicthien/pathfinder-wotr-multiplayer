using ProtoBuf;

namespace WOTRMultiplayer.Networking.Messages.Contracts
{
    [ProtoContract]
    public class NetworkAbilityResource
    {
        [ProtoMember(1)] public string BlueprintId { get; set; }
        [ProtoMember(2)] public int Amount { get; set; }
        [ProtoMember(3)] public int RetainCount { get; set; }
    }
}
