using ProtoBuf;

namespace WOTRMultiplayer.Networking.Messages.Contracts
{
    [ProtoContract]
    public class NetworkStatValues
    {
        [ProtoMember(1)] public int BaseValue { get; set; }
        [ProtoMember(2)] public int ModifiedValue { get; set; }
        [ProtoMember(3)] public int ModifiedValueRaw { get; set; }
        [ProtoMember(4)] public int PermanentValue { get; set; }
    }

    [ProtoContract]
    public class NetworkUnitVitals
    {
        [ProtoMember(1)] public int Damage { get; set; }
        [ProtoMember(2)] public int LifeState { get; set; }
        [ProtoMember(3)] public bool IsFinallyDead { get; set; }
        [ProtoMember(4)] public NetworkStatValues HitPoints { get; set; }
        [ProtoMember(5)] public NetworkStatValues TemporaryHitPoints { get; set; }
        [ProtoMember(6)] public NetworkStatValues NonLethal { get; set; }
        [ProtoMember(7)] public bool IsInCombat { get; set; }
        [ProtoMember(8)] public bool GroupCounterCaptured { get; set; }
        [ProtoMember(9)] public int GroupCombatCounter { get; set; }
    }
}
