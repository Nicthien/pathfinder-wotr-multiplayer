using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyLevelingClassArchetypeSelected)]
    public class NotifyLevelingClassArchetypeSelected : IForwardableMessage, ILevelingMessage
    {
        [ProtoMember(100)]
        public NetworkLevelingIdentity Leveling { get; set; }

        [ProtoMember(1)]
        [LogMe]
        public NetworkLevelingArchetype Archetype { get; set; }
    }
}
