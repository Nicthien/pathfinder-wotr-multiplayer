namespace WOTRMultiplayer.Entities.Units
{
    public class NetworkUnitDescriptor
    {
        public int Damage { get; set; }

        public NetworkUnitVitals Vitals { get; set; }

        public System.Collections.Generic.List<NetworkCalculatedStat> CalculatedStats { get; set; }

        public NetworkCharacterStats Stats { get; set; }

        public NetworkUnitState State { get; set; }

        public bool ResourcesCaptured { get; set; }

        public System.Collections.Generic.List<NetworkAbilityResource> Resources { get; set; } = [];

        public bool SpellbooksCaptured { get; set; }

        public System.Collections.Generic.List<NetworkSpellbookState> Spellbooks { get; set; } = [];
    }
}
