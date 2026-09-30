using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    public enum RealTimeModeTransitionStage
    {
        Prepare = 1,
        Commit = 2,
        Complete = 3,
        Abort = 4
    }

    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyRealTimeModeTransition)]
    public class NotifyRealTimeModeTransition
    {
        [ProtoMember(1)]
        [LogMe]
        public long Epoch { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public int Stage { get; set; }

        [ProtoMember(3)]
        [LogMe]
        public int Mode { get; set; }

        [ProtoMember(4)]
        [LogMe]
        public ulong CanonicalStateHash { get; set; }

        [ProtoMember(5)]
        public NetworkCombatState State { get; set; }
    }
}
