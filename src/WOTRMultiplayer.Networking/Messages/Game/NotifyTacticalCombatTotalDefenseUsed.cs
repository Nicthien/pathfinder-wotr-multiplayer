using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyTacticalCombatTotalDefenseUsed)]
    public class NotifyTacticalCombatTotalDefenseUsed : IForwardableMessage
    {
        [ProtoMember(1)]
        [LogMe]
        public string UnitId { get; set; }
    }
}
