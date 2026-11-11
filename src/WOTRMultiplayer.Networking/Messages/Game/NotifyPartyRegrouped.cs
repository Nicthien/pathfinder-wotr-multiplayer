using System.Collections.Generic;
using ProtoBuf;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    public sealed class PartyRegroupPosition
    {
        [ProtoMember(1)] public string UnitId { get; set; }
        [ProtoMember(2)] public float X { get; set; }
        [ProtoMember(3)] public float Y { get; set; }
        [ProtoMember(4)] public float Z { get; set; }
        [ProtoMember(5)] public float Orientation { get; set; }
        [ProtoMember(6)] public string Scene { get; set; }
    }

    [ProtoContract, MessageType((int)MessageTypes.Game.NotifyPartyRegrouped)]
    public sealed class NotifyPartyRegrouped
    {
        [ProtoMember(1)] public string SessionId { get; set; }
        [ProtoMember(2)] public int LoadSeed { get; set; }
        [ProtoMember(3)] public int AreaSeed { get; set; }
        [ProtoMember(4)] public long RequesterId { get; set; }
        [ProtoMember(5)] public bool Success { get; set; }
        [ProtoMember(6)] public List<PartyRegroupPosition> Positions { get; set; } = new();
        [ProtoMember(7)] public bool IsAreaTransition { get; set; }
        [ProtoMember(8)] public string AreaPartId { get; set; }
        [ProtoMember(9)] public long Sequence { get; set; }
    }
}
