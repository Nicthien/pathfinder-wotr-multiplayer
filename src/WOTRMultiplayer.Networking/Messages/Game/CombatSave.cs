using ProtoBuf;
namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract, MessageType((int)MessageTypes.Game.NotifyCombatSave)]
    public sealed class NotifyCombatSave
    {
        [ProtoMember(1)] public string SessionId { get; set; }
        [ProtoMember(2)] public int SessionSeed { get; set; }
        [ProtoMember(3)] public int LoadedSaveSeed { get; set; }
        [ProtoMember(4)] public long Epoch { get; set; }
        [ProtoMember(5)] public string Operation { get; set; }
        [ProtoMember(6)] public string Stage { get; set; }
        [ProtoMember(7)] public string Result { get; set; }
    }
    [ProtoContract, MessageType((int)MessageTypes.Game.ClientCombatSaveAck)]
    public sealed class ClientCombatSaveAck
    {
        [ProtoMember(1)] public NotifyCombatSave Identity { get; set; }
        [ProtoMember(2)] public string Digest { get; set; }
        [ProtoMember(3)] public string Refusal { get; set; }
    }
}
