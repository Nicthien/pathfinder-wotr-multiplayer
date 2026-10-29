using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.ClientRealTimePauseRequested)]
    public class ClientRealTimePauseRequested
    {
        [ProtoMember(1)]
        [LogMe]
        public long Epoch { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public long RequestSequence { get; set; }

        [ProtoMember(3)]
        [LogMe]
        public long PlayerId { get; set; }

        [ProtoMember(4)]
        [LogMe]
        public bool WantsPause { get; set; }
    }
}
