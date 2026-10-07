using System.Collections.Generic;

namespace WOTRMultiplayer.Entities.Units
{
    public class NetworkSpellbookState
    {
        public string BlueprintId { get; set; }
        public List<int> SpontaneousSlots { get; set; } = [];
        public List<NetworkPreparedSpellState> PreparedSlots { get; set; } = [];
    }

    public class NetworkPreparedSpellState
    {
        public int Level { get; set; }
        public int Index { get; set; }
        public int Type { get; set; }
        public string SpellBlueprintId { get; set; }
        public int Metamagic { get; set; }
        public bool Available { get; set; }
        public bool IsOpposition { get; set; }
    }
}
