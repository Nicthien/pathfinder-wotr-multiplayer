using ProtoBuf;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyFeatureControlRequestResolved)]
    public class NotifyFeatureControlRequestResolved
    {
        [ProtoMember(1)] public string Feature { get; set; }
        [ProtoMember(2)] public long RequesterId { get; set; }
        [ProtoMember(3)] public long Sequence { get; set; }
        [ProtoMember(4)] public bool Accepted { get; set; }
        [ProtoMember(5)] public string Reason { get; set; }
        [ProtoMember(6)] public string SessionId { get; set; }
    }
}
