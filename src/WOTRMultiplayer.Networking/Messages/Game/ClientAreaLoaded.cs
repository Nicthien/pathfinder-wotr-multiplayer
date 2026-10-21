using ProtoBuf;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract]
    [MessageType((int)MessageTypes.Game.ClientAreaLoaded)]
    public class ClientAreaLoaded
    {
        [ProtoMember(1)] public string SessionId { get; set; }
        [ProtoMember(2)] public int LoadedSaveSeed { get; set; }
        [ProtoMember(3)] public string AreaId { get; set; }
    }
}
