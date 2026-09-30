using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyRealTimeCombatPreference)]
    public class NotifyRealTimeCombatPreference
    {
        [ProtoMember(1)]
        [LogMe]
        public long Sequence { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public int PreferredMode { get; set; }

        [ProtoMember(3)]
        [LogMe]
        public int PausePolicy { get; set; }
    }
}
