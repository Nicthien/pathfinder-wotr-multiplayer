using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyGlobalMapLocationMessageShown)]
    public class NotifyGlobalMapLocationMessageShown : IForwardableMessage
    {
        [ProtoMember(1)]
        [LogMe]
        public long PlayerId { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public bool ShouldManuallyTriggerMessage { get; set; }

        [ProtoMember(3), LogMe]
        public NetworkGlobalMapLocation Location { get; set; }

        [ProtoMember(4), LogMe]
        public string Scope { get; set; }
    }
}
