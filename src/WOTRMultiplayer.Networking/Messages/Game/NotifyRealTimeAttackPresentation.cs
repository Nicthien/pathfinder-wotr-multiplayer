using ProtoBuf;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    // Host-only. This message starts a visual handle, never a UnitAttack command.
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyRealTimeAttackPresentation)]
    public class NotifyRealTimeAttackPresentation
    {
        [ProtoMember(1)] public long Epoch { get; set; }
        [ProtoMember(2)] public long Sequence { get; set; }
        [ProtoMember(3)] public string UnitId { get; set; }
        [ProtoMember(4)] public string TargetUnitId { get; set; }
        [ProtoMember(5)] public int AnimationType { get; set; }
        [ProtoMember(6)] public int WeaponStyle { get; set; }
        [ProtoMember(7)] public int Variant { get; set; }
        [ProtoMember(8)] public float SpeedScale { get; set; }
        [ProtoMember(9)] public float TargetDistance { get; set; }
        [ProtoMember(10)] public bool IsCharge { get; set; }
        [ProtoMember(11)] public bool IsRend { get; set; }
        [ProtoMember(12)] public bool IsMounted { get; set; }
        [ProtoMember(13)] public int SpecialAttackType { get; set; }
        [ProtoMember(14)] public long HostGameTimeTicks { get; set; }
        [ProtoMember(15)] public string SessionId { get; set; }
        [ProtoMember(16)] public int LoadedSaveSeed { get; set; }
        [ProtoMember(17)] public string ActionId { get; set; }
        [ProtoMember(18)] public int Stage { get; set; }
    }
}
