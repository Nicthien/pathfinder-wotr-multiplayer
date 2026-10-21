using System.Collections.Generic;
using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyCombatTurnStartSynchronizationRequired)]
    public class NotifyCombatTurnStartSynchronizationRequired
    {
        [ProtoMember(1)]
        public NetworkCombatState CombatState { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public List<NetworkAreaEffect> TriggeredAreaEffects { get; set; } = [];
        [ProtoMember(3)] public string SessionId { get; set; }
        [ProtoMember(4)] public int LoadedSaveSeed { get; set; }
        [ProtoMember(5)] public int CombatSeed { get; set; }
        [ProtoMember(6)] public int Round { get; set; }
        [ProtoMember(7)] public string UnitId { get; set; }
    }
}
