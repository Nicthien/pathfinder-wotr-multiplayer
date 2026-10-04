using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyRealTimeCombatSnapshot)]
    public class NotifyRealTimeCombatSnapshot
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
        public NetworkCombatState State { get; set; }

        [ProtoMember(5)]
        [LogMe]
        public int ResponseTimeMs { get; set; }

        [ProtoMember(6)]
        [LogMe]
        public long MissingReplies { get; set; }

        [ProtoMember(7)]
        [LogMe]
        public string SessionId { get; set; }

        [ProtoMember(8)]
        [LogMe]
        public int LoadedSaveSeed { get; set; }

        [ProtoMember(9)]
        [LogMe]
        public long IncludedEventSequence { get; set; }
    }
}
