using System;

namespace WOTRMultiplayer.Services
{
    public static class RealTimeVisualInterpolation
    {
        public static float Progress(DateTime started, DateTime now, TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero) return 1f;
            if (now <= started) return 0f;
            var value = (now - started).TotalMilliseconds / duration.TotalMilliseconds;
            if (value <= 0) return 0f;
            if (value >= 1) return 1f;
            // Smoothstep avoids a visible jolt at each authoritative checkpoint.
            return (float)(value * value * (3d - 2d * value));
        }
    }
}
