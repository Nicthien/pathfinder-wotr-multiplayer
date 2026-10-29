namespace WOTRMultiplayer.Services.CombatSaving
{
    internal static class CombatSaveActivation
    {
        // Enabled in ordinary profiles at Nicolas's explicit request. This is not
        // runtime validation: host authority, stable-turn and digest checks remain.
        internal static bool Enabled => true;
    }
}
