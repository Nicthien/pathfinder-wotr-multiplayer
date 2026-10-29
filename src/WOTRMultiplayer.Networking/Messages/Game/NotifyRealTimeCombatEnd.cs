using ProtoBuf;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    public enum RealTimeCombatEndStage { Prepare = 1, Commit = 2, Finalize = 3 }

    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyRealTimeCombatEnd)]
    public class NotifyRealTimeCombatEnd
    {
        [ProtoMember(1)] public string SessionId { get; set; }
        [ProtoMember(2)] public int LoadedSaveSeed { get; set; }
        [ProtoMember(3)] public long Epoch { get; set; }
        [ProtoMember(4)] public string EndId { get; set; }
        [ProtoMember(5)] public int Stage { get; set; }
        [ProtoMember(6)] public ulong CanonicalStateHash { get; set; }
        [ProtoMember(7)] public NetworkCombatState State { get; set; }
    }

    [ProtoContract]
    [MessageType((int)MessageTypes.Game.ClientRealTimeCombatEndAck)]
    public class ClientRealTimeCombatEndAck
    {
        [ProtoMember(1)] public string SessionId { get; set; }
        [ProtoMember(2)] public int LoadedSaveSeed { get; set; }
        [ProtoMember(3)] public long Epoch { get; set; }
        [ProtoMember(4)] public string EndId { get; set; }
        [ProtoMember(5)] public int Stage { get; set; }
        [ProtoMember(6)] public ulong CanonicalStateHash { get; set; }
        [ProtoMember(7)] public bool Applied { get; set; }
        [ProtoMember(8)] public string Reason { get; set; }
    }
}
