using System;

namespace WOTRMultiplayer.Services
{
    // Synchronous Unity-thread capture only. Ordinary TB snapshots must not
    // depend on presentation-context serialization succeeding.
    public sealed class RealTimeCaptureScope : IDisposable
    {
        [ThreadStatic] private static int _depth;
        private bool _disposed;
        public static bool Active => _depth > 0;
        public RealTimeCaptureScope() { _depth++; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _depth--;
        }
    }

    // The native save format does not persist AbilityExecutionContext.Ability
    // for every long-lived buff. Combat-save verification therefore captures
    // only the stable source blueprint in that presentation-only reference.
    // Ordinary RTwP checkpoints retain the complete ability payload.
    public sealed class CombatSaveDigestScope : IDisposable
    {
        [ThreadStatic] private static int _depth;
        private bool _disposed;
        public static bool Active => _depth > 0;
        public CombatSaveDigestScope() { _depth++; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _depth--;
        }
    }
}
