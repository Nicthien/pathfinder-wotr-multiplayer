using System.Collections.Generic;
using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;

namespace WOTRMultiplayer.Networking.Messages.Lobby
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Lobby.NotifyAdvancedControlsChanged)]
    public class NotifyAdvancedControlsChanged
    {
        [ProtoMember(1)]
        [LogMe]
        public Dictionary<string, long> Features { get; set; } = [];

        [ProtoMember(2)]
        [LogMe]
        public Dictionary<string, long> PendingFeatures { get; set; } = [];

        [ProtoMember(3)]
        [LogMe]
        public Dictionary<string, long> PendingSequences { get; set; } = [];
    }
}
