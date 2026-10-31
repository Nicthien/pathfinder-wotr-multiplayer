using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyLevelingStarted)]
    public class NotifyLevelingStarted : ILevelingMessage
    {
        [ProtoMember(3)]
        public bool IsForced { get; set; }

        [ProtoMember(100)]
        public NetworkLevelingIdentity Leveling { get; set; }

        [ProtoMember(1)]
        [LogMe]
        public string UnitId { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public string Type { get; set; }
    }
}
