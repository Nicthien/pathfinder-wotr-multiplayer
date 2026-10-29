using System.Collections.Generic;
using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyRealTimePauseChanged)]
    public class NotifyRealTimePauseChanged
    {
        [ProtoMember(1)]
        [LogMe]
        public long Epoch { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public long Sequence { get; set; }

        [ProtoMember(3)]
        [LogMe]
        public bool IsPaused { get; set; }

        [ProtoMember(4)]
        [LogMe]
        public int Policy { get; set; }

        [ProtoMember(5)]
        public List<long> PausedPlayers { get; set; } = [];

        [ProtoMember(6)]
        public List<long> ResumeVotes { get; set; } = [];
    }
}
