namespace WOTRMultiplayer.Entities.Combat.RealTime
{
    public class RealTimeCommandEnvelope
    {
        public long Epoch { get; set; }

        public long Sequence { get; set; }

        public long PlayerId { get; set; }

        public string UnitId { get; set; }

        public RealTimeCommandKind Kind { get; set; }

        public string Payload { get; set; }

        public string SessionId { get; set; }

        public int LoadedSaveSeed { get; set; }

        public string CommandId { get; set; }

        public ulong ObservedStateHash { get; set; }
    }
}
