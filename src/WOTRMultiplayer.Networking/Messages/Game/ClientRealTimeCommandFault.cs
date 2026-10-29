using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.ClientRealTimeCommandFault)]
    public class ClientRealTimeCommandFault
    {
        [ProtoMember(1), LogMe]
        public long Epoch { get; set; }

        [ProtoMember(2), LogMe]
        public long AuthoritativeSequence { get; set; }

        [ProtoMember(3), LogMe]
        public string Reason { get; set; }
    }
}
