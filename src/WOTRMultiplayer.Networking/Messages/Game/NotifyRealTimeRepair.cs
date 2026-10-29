using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    public enum RealTimeRepairStage
    {
        Prepare = 1,
        Verify = 2,
        Apply = 3,
        Fallback = 4,
        Resume = 5,
        Stopped = 6
    }

    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyRealTimeRepair)]
    public class NotifyRealTimeRepair
    {
        [ProtoMember(1)]
        [LogMe]
        public long Epoch { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public int Stage { get; set; }

        [ProtoMember(3)]
        [LogMe]
        public ulong CanonicalStateHash { get; set; }

        [ProtoMember(4)]
        [LogMe]
        public int Mode { get; set; }

        [ProtoMember(5)]
        public NetworkCombatState State { get; set; }

        [ProtoMember(6)] public string Reason { get; set; }
        [ProtoMember(7)] public string SessionId { get; set; }
        [ProtoMember(8)] public int LoadedSaveSeed { get; set; }
    }
}
