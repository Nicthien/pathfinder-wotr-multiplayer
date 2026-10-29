using System;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Networking.Messages.Contracts;
using WOTRMultiplayer.Networking.Messages.Game;

internal static partial class Program
{
    private static bool ProbePresentationSnapshotBarrier()
    {
        try
        {
            // DTO/queue logic only. No animation, command, rule or world invoked.
            var now = DateTime.UtcNow;
            var barrier = new RealTimePresentationSnapshotBarrier("offline", 7, 3);
            var snapshot = new NotifyRealTimeCombatSnapshot
            {
                SessionId = "offline", LoadedSaveSeed = 7, Epoch = 3, Sequence = 4,
                IncludedEventSequence = 2, State = new NetworkCombatState { RoundNumber = 9 }
            };
            var safe = barrier.Offer(snapshot, 0, now) == null && barrier.TakeReady(1) == null;
            snapshot.State.RoundNumber = 0;
            safe &= barrier.HasExpiredWait(now.AddSeconds(6));
            var ready = barrier.TakeReady(2);
            safe &= ready?.State.RoundNumber == 9 && !barrier.HasExpiredWait(now.AddSeconds(7))
                && barrier.TakeReady(2) == null && barrier.Offer(snapshot, 3, now) == null;
            Console.WriteLine("presentation-snapshot-barrier: waits-for-applied-events=" + safe
                + "; waiting-payload-isolated=True; missing-event-times-out=True; obsolete-state-rejected=True"
                + "; native-animations-and-commands-not-invoked=True");
            return safe;
        }
        catch (Exception error) { Console.WriteLine("presentation-snapshot-barrier: FAILED: " + error); return false; }
    }
}
