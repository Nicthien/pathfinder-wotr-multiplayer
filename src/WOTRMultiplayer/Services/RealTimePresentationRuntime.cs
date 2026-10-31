namespace WOTRMultiplayer.Services
{
    // Separate build until the A0 real-engine gate is satisfied. The existing
    // installed-code fingerprint prevents joining a differently compiled peer.
    public static class RealTimePresentationRuntime
    {
        [System.ThreadStatic]
        private static int _authoritativePresentationDepth;
#if RTWP_HOST_PRESENTATION_PROTOTYPE
        public static bool Enabled { get; } = true;
#else
        public static bool Enabled { get; } = false;
#endif
        public static bool IsClientActive => Enabled
            // Native workers can outlive the network session. Disconnect must
            // revoke hydration/stash writes, not re-enable rules or AI while
            // the protected client world is still being reconstructed.
            && (GameInteraction.RealTimeNativeLoadLease.IsActive
                || Main.Multiplayer?.IsClientSaveTransferActive == true
                || Main.Multiplayer?.IsRealTimeClientCombatActive == true);

        // Explicit network-result presentation may construct native Rulebook
        // objects for combat text/UI. Their callbacks must remain suppressed on
        // the client, but that expected suppression is not an autonomous-rule
        // fault and must not initiate a repair.
        public static bool IsApplyingAuthoritativePresentation => _authoritativePresentationDepth > 0;

        public static System.IDisposable BeginAuthoritativePresentation()
        {
            _authoritativePresentationDepth++;
            return new PresentationScope();
        }

        private sealed class PresentationScope : System.IDisposable
        {
            private bool _disposed;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (--_authoritativePresentationDepth < 0)
                {
                    _authoritativePresentationDepth = 0;
                    throw new System.InvalidOperationException("authoritative-presentation-scope-underflow");
                }
            }
        }
    }
}
