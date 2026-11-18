using ProtoBuf;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.ClientFeatureControlRequested)]
    public class ClientFeatureControlRequested
    {
        [ProtoMember(1)] public string Feature { get; set; }
        [ProtoMember(2)] public string SessionId { get; set; }
        [ProtoMember(3)] public long Sequence { get; set; }
    }
}
