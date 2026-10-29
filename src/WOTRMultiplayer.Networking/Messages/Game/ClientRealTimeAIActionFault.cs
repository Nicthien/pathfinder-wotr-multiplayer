using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.ClientRealTimeAIActionFault)]
    public class ClientRealTimeAIActionFault
    {
        [ProtoMember(1)]
        [LogMe]
        public long Epoch { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public string UnitId { get; set; }

        [ProtoMember(3)]
        [LogMe]
        public string ActionId { get; set; }

        [ProtoMember(4)]
        [LogMe]
        public string Reason { get; set; }
    }
}
