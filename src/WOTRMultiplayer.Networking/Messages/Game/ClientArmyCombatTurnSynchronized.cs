using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.ClientArmyCombatTurnSynchronized)]
    public class ClientArmyCombatTurnSynchronized
    {
        [ProtoMember(1)]
        [LogMe]
        public int TurnNumber { get; set; }
    }
}
