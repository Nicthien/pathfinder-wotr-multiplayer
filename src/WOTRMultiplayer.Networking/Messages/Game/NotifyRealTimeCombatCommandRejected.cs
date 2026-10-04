using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyRealTimeCombatCommandRejected)]
    public class NotifyRealTimeCombatCommandRejected
    {
        [ProtoMember(1), LogMe] public string SessionId { get; set; }
        [ProtoMember(2), LogMe] public int LoadedSaveSeed { get; set; }
        [ProtoMember(3), LogMe] public long Epoch { get; set; }
        [ProtoMember(4), LogMe] public long PlayerId { get; set; }
        [ProtoMember(5), LogMe] public long ClientSequence { get; set; }
        [ProtoMember(6), LogMe] public string CommandId { get; set; }
        [ProtoMember(7), LogMe] public int Reason { get; set; }
    }
}
