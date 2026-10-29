using WOTRMultiplayer.Entities;

namespace WOTRMultiplayer.Entities.Combat.RealTime
{
    public class NetworkProjectileState
    {
        public string Id { get; set; }
        public string BlueprintId { get; set; }
        public NetworkVector3 Position { get; set; }
        public float RotationX { get; set; }
        public float RotationY { get; set; }
        public float RotationZ { get; set; }
        public float RotationW { get; set; } = 1;
        public bool IsHit { get; set; }

        public NetworkProjectileState Copy() => new()
        {
            Id = Id, BlueprintId = BlueprintId,
            Position = Position == null ? null : new NetworkVector3(Position.X, Position.Y, Position.Z),
            RotationX = RotationX, RotationY = RotationY, RotationZ = RotationZ, RotationW = RotationW,
            IsHit = IsHit
        };
    }
}
