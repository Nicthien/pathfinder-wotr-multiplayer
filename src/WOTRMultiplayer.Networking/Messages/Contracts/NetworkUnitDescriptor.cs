using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Contracts
{
    [ProtoContract]
    public class NetworkUnitDescriptor
    {
        [ProtoMember(1)]
        [LogMe]
        public int Damage { get; set; }

        [ProtoMember(2)]
        public NetworkCharacterStats Stats { get; set; }

        [ProtoMember(3)]
        public NetworkUnitState State { get; set; }

        [ProtoMember(4)]
        public System.Collections.Generic.List<NetworkAbilityResource> Resources { get; set; } = [];

        [ProtoMember(5)]
        public bool ResourcesCaptured { get; set; }

        [ProtoMember(6)]
        public System.Collections.Generic.List<NetworkSpellbookState> Spellbooks { get; set; } = [];

        [ProtoMember(7)]
        public bool SpellbooksCaptured { get; set; }

        [ProtoMember(8)] public NetworkUnitVitals Vitals { get; set; }

        [ProtoMember(9)] public System.Collections.Generic.List<NetworkCalculatedStat> CalculatedStats { get; set; }
    }
}
