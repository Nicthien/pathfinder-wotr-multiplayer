using ProtoBuf;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    public enum RealTimeAuthoritativeEventKind
    {
        AttackPresentation = 1,
        AbilityPresentation = 2,
        ProjectilePresentation = 3
    }

    [ProtoContract]
    public class RealTimeAttackPresentationPayload
    {
        [ProtoMember(1)] public int Stage { get; set; }
        [ProtoMember(2)] public int AnimationType { get; set; }
        [ProtoMember(3)] public int WeaponStyle { get; set; }
        [ProtoMember(4)] public int Variant { get; set; }
        [ProtoMember(5)] public float SpeedScale { get; set; }
        [ProtoMember(6)] public float TargetDistance { get; set; }
        [ProtoMember(7)] public bool IsCharge { get; set; }
        [ProtoMember(8)] public bool IsRend { get; set; }
        [ProtoMember(9)] public bool IsMounted { get; set; }
        [ProtoMember(10)] public int SpecialAttackType { get; set; }
    }

    // Reliable host-only event. Applying its presentation payload must never
    // create a local combat command or a local rules consequence.
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyRealTimeAuthoritativeEvent)]
    public class NotifyRealTimeAuthoritativeEvent
    {
        [ProtoMember(1)] public long Epoch { get; set; }
        [ProtoMember(2)] public long Sequence { get; set; }
        [ProtoMember(3)] public string SessionId { get; set; }
        [ProtoMember(4)] public int LoadedSaveSeed { get; set; }
        [ProtoMember(5)] public string EventId { get; set; }
        [ProtoMember(6)] public string ActionId { get; set; }
        [ProtoMember(7)] public int Kind { get; set; }
        [ProtoMember(8)] public long HostGameTimeTicks { get; set; }
        [ProtoMember(9)] public string UnitId { get; set; }
        [ProtoMember(10)] public string TargetUnitId { get; set; }
        [ProtoMember(11)] public RealTimeAttackPresentationPayload Attack { get; set; }
        [ProtoMember(12)] public RealTimeAbilityPresentationPayload Ability { get; set; }
        [ProtoMember(13)] public RealTimeProjectilePresentationPayload Projectile { get; set; }
    }

    [ProtoContract]
    public class RealTimeAbilityPresentationPayload
    {
        [ProtoMember(1)] public int Stage { get; set; }
        [ProtoMember(2)] public string BlueprintId { get; set; }
        [ProtoMember(3)] public int AnimationType { get; set; }
        [ProtoMember(4)] public int CastStyle { get; set; }
        [ProtoMember(5)] public int SpecialBehaviour { get; set; }
        [ProtoMember(6)] public float CastingTime { get; set; }
        [ProtoMember(7)] public float SpeedScale { get; set; }
        [ProtoMember(8)] public int WeaponStyle { get; set; }
        [ProtoMember(9)] public float TargetDistance { get; set; }
        [ProtoMember(10)] public bool IsMounted { get; set; }
        [ProtoMember(11)] public float MinimalTransitionOut { get; set; }
    }

    [ProtoContract]
    public class RealTimeProjectilePresentationPayload
    {
        [ProtoMember(1)] public int Stage { get; set; }
        [ProtoMember(2)] public Contracts.NetworkProjectileState State { get; set; }
    }
}
