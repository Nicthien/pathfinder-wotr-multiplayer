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
    }
}
