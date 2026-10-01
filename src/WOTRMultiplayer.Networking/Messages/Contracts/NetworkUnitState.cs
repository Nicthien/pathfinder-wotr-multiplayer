using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Contracts
{
    [ProtoContract]
    public class NetworkUnitState
    {
        [ProtoMember(1)]
        [LogMe]
        public bool IsCharging { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public NetworkUnitProneState Prone { get; set; }

        [ProtoMember(3)]
        [LogMe]
        public bool IsFinallyDead { get; set; }
    }
}
