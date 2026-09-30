using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.ClientRealTimeModeTransitionAck)]
    public class ClientRealTimeModeTransitionAck
    {
        [ProtoMember(1)]
        [LogMe]
        public long Epoch { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public int Stage { get; set; }

        [ProtoMember(3)]
        [LogMe]
        public long PlayerId { get; set; }

        [ProtoMember(4)]
        [LogMe]
        public ulong CanonicalStateHash { get; set; }

        [ProtoMember(5)]
        [LogMe]
        public bool Applied { get; set; }
    }
}
