using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyAIActionSelected)]
    public class NotifyAIActionSelected
    {
        [ProtoMember(1)]
        [LogMe]
        public NetworkAIAction Action { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public long RealTimeEpoch { get; set; }

        [ProtoMember(3)]
        [LogMe]
        public long RealTimeSequence { get; set; }
    }
}
