namespace WOTRMultiplayer.Entities.Combat.RealTime
{
    public enum CombatSynchronizationPhase
    {
        Stable = 0,
        PreparingModeSwitch = 1,
        Synchronizing = 2,
        Repairing = 3,
        FallingBackToTurnBased = 4,
    }
}
