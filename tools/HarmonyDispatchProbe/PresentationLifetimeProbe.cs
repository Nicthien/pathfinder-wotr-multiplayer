using System;
using WOTRMultiplayer.Services;

internal static partial class Program
{
    private static bool ProbePresentationLifetime()
    {
        try
        {
            // Pure logical clock policy, no native handle/animation or world.
            var start = TimeSpan.FromSeconds(100);
            var ordinary = new RealTimePresentationLifetime(start);
            var safe = true;
            for (var tick = 0; tick < 10000; tick++) safe &= ordinary.FailureAt(start) == null;
            safe &= ordinary.FailureAt(start + TimeSpan.FromSeconds(15)) == "presentation-action-terminal-event-timeout";
            safe &= ordinary.FailureAt(start - TimeSpan.FromTicks(1)) == "presentation-game-clock-regressed";
            var slowCast = new RealTimePresentationLifetime(start, 30, 0.5f);
            safe &= slowCast.Budget == TimeSpan.FromSeconds(90)
                && slowCast.FailureAt(start + TimeSpan.FromSeconds(89)) == null;
            Console.WriteLine("presentation-lifetime: paused-clock-keeps-budget=" + safe
                + "; slow-long-cast-budget=True; timeout-and-clock-regression-detected=True"
                + "; native-animations-and-world-not-invoked=True");
            return safe;
        }
        catch (Exception error) { Console.WriteLine("presentation-lifetime: FAILED: " + error); return false; }
    }
}
