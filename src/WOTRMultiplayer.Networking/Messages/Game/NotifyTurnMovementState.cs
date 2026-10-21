using ProtoBuf;
namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract, MessageType((int)MessageTypes.Game.NotifyTurnMovementState)]
    public sealed class NotifyTurnMovementState
    {
        [ProtoMember(1)] public string Session { get; set; }
        [ProtoMember(2)] public int SessionSeed { get; set; }
        [ProtoMember(3)] public int LoadSeed { get; set; }
        [ProtoMember(4)] public int TurnSeed { get; set; }
        [ProtoMember(5)] public string Unit { get; set; }
        [ProtoMember(6)] public long Sequence { get; set; }
        [ProtoMember(7)] public string State { get; set; }
        [ProtoMember(8)] public long Movement { get; set; }
        [ProtoMember(9)] public int CombatSeed { get; set; }
        [ProtoMember(10)] public long Epoch { get; set; }
    }
}
