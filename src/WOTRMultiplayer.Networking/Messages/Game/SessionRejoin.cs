using ProtoBuf;
namespace WOTRMultiplayer.Networking.Messages.Game
{
    [ProtoContract, MessageType((int)MessageTypes.Game.NotifySessionRejoin)]
    public class NotifySessionRejoin
    {
        [ProtoMember(1)] public NotifyRealTimeNativeCheckpoint Manifest { get; set; }
        [ProtoMember(2)] public string Stage { get; set; }
        [ProtoMember(3)] public string AreaId { get; set; }
        [ProtoMember(4)] public int AreaSeed { get; set; }
        [ProtoMember(5)] public int CombatSeed { get; set; }
        [ProtoMember(6)] public int CombatRound { get; set; }
        [ProtoMember(7)] public bool RealTime { get; set; }
        [ProtoMember(8)] public bool Paused { get; set; }
        [ProtoMember(9)] public byte[] Reference { get; set; }
        [ProtoMember(10)] public string Reason { get; set; }
        [ProtoMember(11)] public string ConnectionToken { get; set; }
    }
    [ProtoContract, MessageType((int)MessageTypes.Game.NotifySessionRejoinChunk)]
    public class NotifySessionRejoinChunk
    {
        [ProtoMember(1)] public NotifyRealTimeNativeCheckpointChunk Chunk { get; set; }
    }
    [ProtoContract, MessageType((int)MessageTypes.Game.ClientSessionRejoinAck)]
    public class ClientSessionRejoinAck
    {
        [ProtoMember(1)] public ClientRealTimeNativeCheckpointAck Ack { get; set; }
    }
}
