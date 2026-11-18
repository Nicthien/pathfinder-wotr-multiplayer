namespace WOTRMultiplayer.Entities
{
    public class NetworkFeatureControlTransfer
    {
        public long RequesterId { get; set; }

        public long Sequence { get; set; }

        public string SessionId { get; set; }

        public long CreatedUtcTicks { get; set; }
    }
}
