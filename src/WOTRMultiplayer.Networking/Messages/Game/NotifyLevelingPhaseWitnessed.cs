using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyLevelingPhaseWitnessed)]
    public class NotifyLevelingPhaseWitnessed : IForwardableMessage, ILevelingMessage
    {
        [ProtoMember(100)]
        public NetworkLevelingIdentity Leveling { get; set; }

        [ProtoMember(1)]
        [LogMe]
        public long PlayerId { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public NetworkLevelingPhase Phase { get; set; }
    }
}
