using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    // Host-only, deliberately not forwardable from a client.
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyRealTimeUnitKilled)]
    public class NotifyRealTimeUnitKilled
    {
        [ProtoMember(1)]
        [LogMe]
        public long Epoch { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public long Sequence { get; set; }

        [ProtoMember(3)]
        [LogMe]
        public string UnitId { get; set; }

        [ProtoMember(4)]
        public string SessionId { get; set; }

        [ProtoMember(5)]
        public int LoadedSaveSeed { get; set; }
    }
}
