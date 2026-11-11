using ProtoBuf;
using WOTRMultiplayer.Logging.Attributes;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.NotifyDialogStarted)]
    public class NotifyDialogStarted
    {
        [ProtoMember(1)]
        [LogMe]
        public NetworkDialog Dialog { get; set; }

        [ProtoMember(2)] public bool HostOutsideCombat { get; set; }
        [ProtoMember(3)] public string SessionId { get; set; }
        [ProtoMember(4)] public int LoadedSaveSeed { get; set; }
        [ProtoMember(5)] public int AreaSeed { get; set; }
    }
}
