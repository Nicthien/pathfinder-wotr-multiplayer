namespace WOTRMultiplayer.Services
{
    // Separate build until the A0 real-engine gate is satisfied. The existing
    // installed-code fingerprint prevents joining a differently compiled peer.
    public static class RealTimePresentationRuntime
    {
#if RTWP_HOST_PRESENTATION_PROTOTYPE
        public static bool Enabled { get; } = true;
#else
        public static bool Enabled { get; } = false;
#endif
        public static bool IsClientActive => Enabled
            && Main.Multiplayer?.IsRealTimeClientCombatActive == true;
    }
}
