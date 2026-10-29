using System.Collections.Generic;
using ProtoBuf;

namespace WOTRMultiplayer.Networking.Messages.Contracts
{
    [ProtoContract]
    public class NetworkBuffContextState
    {
        [ProtoMember(1)] public string BlueprintId { get; set; }
        [ProtoMember(2)] public string CasterId { get; set; }
        [ProtoMember(3)] public string OwnerId { get; set; }
        [ProtoMember(4)] public bool IsAbilityContext { get; set; }
        [ProtoMember(5)] public NetworkAbility SourceAbility { get; set; }
        [ProtoMember(6)] public NetworkAbilityParams Params { get; set; }
        [ProtoMember(7)] public List<int> Ranks { get; set; } = [];
        [ProtoMember(8)] public List<int> SharedValues { get; set; } = [];
        [ProtoMember(9)] public List<int> Properties { get; set; } = [];
        [ProtoMember(10)] public long SpellDescriptor { get; set; }
        [ProtoMember(11)] public int SpellSchool { get; set; }
        [ProtoMember(12)] public int SpellLevel { get; set; }
        [ProtoMember(13)] public bool DisableFx { get; set; }
        [ProtoMember(14)] public NetworkVector3 Direction { get; set; }
        [ProtoMember(15)] public string SourceItemId { get; set; }
        [ProtoMember(16)] public NetworkTargetWrapper MainTarget { get; set; }
    }
}
