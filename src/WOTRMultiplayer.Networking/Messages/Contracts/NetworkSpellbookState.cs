using System.Collections.Generic;
using ProtoBuf;

namespace WOTRMultiplayer.Networking.Messages.Contracts
{
    [ProtoContract]
    public class NetworkSpellbookState
    {
        [ProtoMember(1)] public string BlueprintId { get; set; }
        [ProtoMember(2)] public List<int> SpontaneousSlots { get; set; } = [];
        [ProtoMember(3)] public List<NetworkPreparedSpellState> PreparedSlots { get; set; } = [];
    }

    [ProtoContract]
    public class NetworkPreparedSpellState
    {
        [ProtoMember(1)] public int Level { get; set; }
        [ProtoMember(2)] public int Index { get; set; }
        [ProtoMember(3)] public int Type { get; set; }
        [ProtoMember(4)] public string SpellBlueprintId { get; set; }
        [ProtoMember(5)] public int Metamagic { get; set; }
        [ProtoMember(6)] public bool Available { get; set; }
        [ProtoMember(7)] public bool IsOpposition { get; set; }
        [ProtoMember(8)] public int MetamagicSpellLevelCost { get; set; }
        [ProtoMember(9)] public int MetamagicHeightenLevel { get; set; }
    }
}
