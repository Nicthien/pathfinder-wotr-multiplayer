using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyInvalidCombatTurnStarted)]
    public class NotifyInvalidCombatTurnStarted
    {
        [ProtoMember(1)]
        [LogMe]
        public string UnitId { get; set; }
        [ProtoMember(2)] public string SessionId { get; set; }
        [ProtoMember(3)] public int LoadedSaveSeed { get; set; }
        [ProtoMember(4)] public int CombatSeed { get; set; }
        [ProtoMember(5)] public int Round { get; set; }
    }
}
