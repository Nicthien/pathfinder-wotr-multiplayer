using ProtoBuf;

namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract, MessageType((int)MessageTypes.Game.ClientPartyRegroupRequested)]
    public sealed class ClientPartyRegroupRequested
    {
        [ProtoMember(1)] public long TargetPlayerId { get; set; }
        [ProtoMember(2)] public string SessionId { get; set; }
        [ProtoMember(3)] public int LoadSeed { get; set; }
        [ProtoMember(4)] public int AreaSeed { get; set; }
    }
}
