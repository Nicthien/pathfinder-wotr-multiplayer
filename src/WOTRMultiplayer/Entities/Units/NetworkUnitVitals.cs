namespace WOTRMultiplayer.Entities.Units
{
    public class NetworkStatValues
    {
        public int BaseValue { get; set; }
        public int ModifiedValue { get; set; }
        public int ModifiedValueRaw { get; set; }
        public int PermanentValue { get; set; }
    }

    public class NetworkUnitVitals
    {
        public int Damage { get; set; }
        public int LifeState { get; set; }
        public bool IsFinallyDead { get; set; }
        public NetworkStatValues HitPoints { get; set; }
        public NetworkStatValues TemporaryHitPoints { get; set; }
        public NetworkStatValues NonLethal { get; set; }
        public bool IsInCombat { get; set; }
        public bool GroupCounterCaptured { get; set; }
        public int GroupCombatCounter { get; set; }
    }
}
