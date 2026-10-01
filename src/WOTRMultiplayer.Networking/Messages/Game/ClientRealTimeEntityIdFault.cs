using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.ClientRealTimeEntityIdFault)]
    public class ClientRealTimeEntityIdFault
    {
        [ProtoMember(1), LogMe]
        public long Epoch { get; set; }

        [ProtoMember(2)]
        public string Key { get; set; }

        [ProtoMember(3), LogMe]
        public long Sequence { get; set; }

        [ProtoMember(4), LogMe]
        public string Reason { get; set; }
    }
}
