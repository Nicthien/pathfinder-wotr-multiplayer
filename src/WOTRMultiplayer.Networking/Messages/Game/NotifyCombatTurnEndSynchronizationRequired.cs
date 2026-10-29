using System.Collections.Generic;
using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyCombatTurnEndSynchronizationRequired)]
    public class NotifyCombatTurnEndSynchronizationRequired
    {
        [ProtoMember(1)]
        [LogMe]
        public List<NetworkUnit> Units { get; set; } = [];

        [ProtoMember(2)]
        [LogMe]
        public string UnitId { get; set; }

        [ProtoMember(3)]
        [LogMe]
        public long SynchronizationSequence { get; set; }
    }
}
