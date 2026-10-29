using System;

namespace WOTRMultiplayer.Services
{
    // Logical game time only. A manual/network pause must not consume this
    // budget. This watchdog detects a missing terminal event; it is not a
    // clock or an animation seek mechanism for native reconstruction.
    public sealed class RealTimePresentationLifetime
    {
        public TimeSpan Started { get; }
        public TimeSpan Budget { get; }

        public RealTimePresentationLifetime(TimeSpan started, float castingTime = 0, float speedScale = 1)
        {
            if (started < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(started));
            if (float.IsNaN(castingTime) || float.IsInfinity(castingTime) || castingTime < 0)
                throw new ArgumentOutOfRangeException(nameof(castingTime));
            if (float.IsNaN(speedScale) || float.IsInfinity(speedScale) || speedScale <= 0 || speedScale > 20)
                throw new ArgumentOutOfRangeException(nameof(speedScale));
            // Never shorten the grace period for a fast animation, and allow
            // slow handles enough logical time to finish their cast/clip.
            var seconds = ((double)castingTime + 15d) / Math.Min((double)speedScale, 1d);
            if (seconds >= TimeSpan.MaxValue.TotalSeconds - 1)
                throw new ArgumentOutOfRangeException(nameof(castingTime));
            Started = started;
            Budget = TimeSpan.FromSeconds(seconds);
        }

        public string FailureAt(TimeSpan gameTime)
        {
            if (gameTime < Started) return "presentation-game-clock-regressed";
            return gameTime - Started >= Budget ? "presentation-action-terminal-event-timeout" : null;
        }
    }
}
