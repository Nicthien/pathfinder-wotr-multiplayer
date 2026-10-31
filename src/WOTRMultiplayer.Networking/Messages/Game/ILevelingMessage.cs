using ProtoBuf;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    // Carried by each choice: forwarding through the host must not erase its destination.
    public interface ILevelingMessage
    {
        NetworkLevelingIdentity Leveling { get; set; }
    }

    [ProtoContract]
    public sealed class NetworkLevelingIdentity
    {
        [ProtoMember(1)] public string Id { get; set; }
        [ProtoMember(2)] public string SessionId { get; set; }
        [ProtoMember(3)] public int? LoadedSaveSeed { get; set; }
        [ProtoMember(4)] public string UnitId { get; set; }
        [ProtoMember(5)] public string Type { get; set; }
        [ProtoMember(6)] public long OwnerId { get; set; }
    }
}
