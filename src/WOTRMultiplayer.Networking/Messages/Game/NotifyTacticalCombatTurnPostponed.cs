using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyTacticalCombatTurnPostponed)]
    public class NotifyTacticalCombatTurnPostponed : IForwardableMessage
    {
        [ProtoMember(1)]
        [LogMe]
        public string UnitId { get; set; }
    }
}
