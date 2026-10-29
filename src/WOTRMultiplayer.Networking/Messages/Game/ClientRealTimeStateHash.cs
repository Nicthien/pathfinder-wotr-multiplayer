using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.ClientRealTimeStateHash)]
    public class ClientRealTimeStateHash
    {
        [ProtoMember(1)]
        [LogMe]
        public long Epoch { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public long Sequence { get; set; }

        [ProtoMember(3)]
        [LogMe]
        public ulong CanonicalStateHash { get; set; }

        [ProtoMember(4)]
        [LogMe]
        public bool Applied { get; set; }

        [ProtoMember(5)]
        [LogMe]
        public string FailureReason { get; set; }
    }
}
