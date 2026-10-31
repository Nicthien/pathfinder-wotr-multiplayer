using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyLevelingEyesColorAppearanceChanged)]
    public class NotifyLevelingEyesColorAppearanceChanged : IForwardableMessage, ILevelingMessage
    {
        [ProtoMember(100)]
        public NetworkLevelingIdentity Leveling { get; set; }

        [ProtoMember(1)]
        [LogMe]
        public string TextureName { get; set; }
    }
}
