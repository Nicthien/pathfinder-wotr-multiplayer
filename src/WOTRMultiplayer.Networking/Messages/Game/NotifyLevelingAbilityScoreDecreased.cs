using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyLevelingAbilityScoreDecreased)]
    public class NotifyLevelingAbilityScoreDecreased : IForwardableMessage, ILevelingMessage
    {
        [ProtoMember(100)]
        public NetworkLevelingIdentity Leveling { get; set; }

        [ProtoMember(1)]
        [LogMe]
        public NetworkLevelingAbilityScore AbilityScore { get; set; }
    }
}
