using ProtoBuf;

namespace WOTRMultiplayer.Networking.Messages.Contracts
{
    [ProtoContract]
    public class NetworkProjectileState
    {
        [ProtoMember(1)] public string Id { get; set; }
        [ProtoMember(2)] public string BlueprintId { get; set; }
        [ProtoMember(3)] public NetworkVector3 Position { get; set; }
        [ProtoMember(4)] public float RotationX { get; set; }
        [ProtoMember(5)] public float RotationY { get; set; }
        [ProtoMember(6)] public float RotationZ { get; set; }
        [ProtoMember(7)] public float RotationW { get; set; } = 1;
        [ProtoMember(8)] public bool IsHit { get; set; }
    }
}
