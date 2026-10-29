using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.ClientRealTimeRepairAck)]
    public class ClientRealTimeRepairAck
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

        [ProtoMember(6)]
        [LogMe]
        public bool TemporalMismatch { get; set; }
        [ProtoMember(7)] public string SessionId { get; set; }
        [ProtoMember(8)] public int LoadedSaveSeed { get; set; }
    }
}
