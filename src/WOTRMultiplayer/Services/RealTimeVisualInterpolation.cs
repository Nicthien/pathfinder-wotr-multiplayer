using System;

namespace WOTRMultiplayer.Services
{
    public static class RealTimeVisualInterpolation
    {
        public static float Progress(DateTime started, DateTime now, TimeSpan duration)
            => Progress(now - started, duration);

        // Game time stops during a shared pause. Visual interpolation must not
        // continue moving a confirmed unit while both players are paused.
        public static float Progress(TimeSpan started, TimeSpan now, TimeSpan duration)
            => Progress(now - started, duration);

        private static float Progress(TimeSpan elapsed, TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero) return 1f;
            if (elapsed <= TimeSpan.Zero) return 0f;
            var value = elapsed.TotalMilliseconds / duration.TotalMilliseconds;
            if (value <= 0) return 0f;
            if (value >= 1) return 1f;
            // Smoothstep avoids a visible jolt at each authoritative checkpoint.
            return (float)(value * value * (3d - 2d * value));
        }
    }
}
