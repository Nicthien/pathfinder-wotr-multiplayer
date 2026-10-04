using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.ClientRealTimeCombatCommand)]
    public class ClientRealTimeCombatCommand
    {
        [ProtoMember(1)]
        [LogMe]
        public long Epoch { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public long Sequence { get; set; }

        [ProtoMember(3)]
        [LogMe]
        public long PlayerId { get; set; }

        [ProtoMember(4)]
        [LogMe]
        public int Kind { get; set; }

        [ProtoMember(5)]
        public NetworkUnitMoveTo Movement { get; set; }

        [ProtoMember(6)]
        public NetworkUnitAttack Attack { get; set; }

        [ProtoMember(7)]
        public NetworkAbilityUse Ability { get; set; }

        [ProtoMember(8)]
        [LogMe]
        public string SessionId { get; set; }

        [ProtoMember(9)]
        [LogMe]
        public int LoadedSaveSeed { get; set; }

        [ProtoMember(10)]
        [LogMe]
        public string CommandId { get; set; }

        [ProtoMember(11)]
        [LogMe]
        public ulong ObservedStateHash { get; set; }
    }
}
