using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyPingedByPlayer)]
    public class NotifyPingedByPlayer : IForwardableMessage
    {
        [ProtoMember(1)]
        [LogMe]
        public long PlayerId { get; set; }

        [ProtoMember(2)]
        [LogMe]
        public NetworkPing Ping { get; set; }

        [ProtoMember(3)]
        public string SessionId { get; set; }

        [ProtoMember(4)]
        public int LoadedSaveSeed { get; set; }

        [ProtoMember(5)]
        public int? AreaSeed { get; set; }
    }
}
