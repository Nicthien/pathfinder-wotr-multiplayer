using System.Collections.Generic;
using WOTRMultiplayer.Entities.Combat;

namespace WOTRMultiplayer.Entities.Units
{
    // Presentation values already calculated by the host. This is not a
    // serialized mechanical context suitable for resuming client simulation.
    public class NetworkBuffContextState
    {
        public string BlueprintId { get; set; }
        public string CasterId { get; set; }
        public string OwnerId { get; set; }
        public bool IsAbilityContext { get; set; }
        public NetworkAbility SourceAbility { get; set; }
        public NetworkAbilityParams Params { get; set; }
        public List<int> Ranks { get; set; } = [];
        public List<int> SharedValues { get; set; } = [];
        public List<int> Properties { get; set; } = [];
        public long SpellDescriptor { get; set; }
        public int SpellSchool { get; set; }
        public int SpellLevel { get; set; }
        public bool DisableFx { get; set; }
        public NetworkVector3 Direction { get; set; }
        public string SourceItemId { get; set; }
        public NetworkTargetWrapper MainTarget { get; set; }
    }
}
