using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Contracts
{
    [ProtoContract]
    public class NetworkUnit
    {
        [ProtoMember(1)]
        [LogMe]
        public string Id { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public NetworkVector3 Position { get; set; }

        [ProtoMember(3)]
        public float Orientation { get; set; }

        [ProtoMember(4)]
        [LogMe]
        public NetworkUnitTurnBasedInfo TurnBasedInfo { get; set; }

        [ProtoMember(5)]
        public NetworkUnitCombatState CombatState { get; set; }

        [ProtoMember(6)]
        public NetworkUnitDescriptor Descriptor { get; set; }

        [ProtoMember(7)]
        public NetworkUnitBuffCollection BuffCollection { get; set; }

        [ProtoMember(8)]
        public NetworkUnitPartKineticist UnitPartKineticist { get; set; }

        [ProtoMember(9)]
        public NetworkUnitPartTacticalCombat UnitPartTacticalCombat { get; set; }

        [ProtoMember(10)]
        public string BlueprintId { get; set; }

        [ProtoMember(11)]
        public string OriginalBlueprintId { get; set; }

        [ProtoMember(12)]
        public string CasterUnitId { get; set; }

        [ProtoMember(13)]
        public string SourceAbilityBlueprintId { get; set; }

        [ProtoMember(14)]
        public string MasterUnitId { get; set; }

        [ProtoMember(15)]
        public string GroupId { get; set; }

        [ProtoMember(16)]
        public string SceneName { get; set; }

        [ProtoMember(17)]
        public bool IsSpottedByPlayer { get; set; }

        public override string ToString()
        {
            return Id?.ToString();
        }
    }
}
