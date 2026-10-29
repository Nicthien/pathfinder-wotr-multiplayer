using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.ClientCombatTurnEndSynchronized)]
    public class ClientCombatTurnEndSynchronized
    {
        [ProtoMember(1)]
        [LogMe]
        public string UnitId { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public long PlayerId { get; set; }

        [ProtoMember(3)]
        [LogMe]
        public long SynchronizationSequence { get; set; }

        [ProtoMember(4)]
        [LogMe]
        public bool Succeeded { get; set; }

        [ProtoMember(5)]
        [LogMe]
        public string FailureReason { get; set; }
    }
}
