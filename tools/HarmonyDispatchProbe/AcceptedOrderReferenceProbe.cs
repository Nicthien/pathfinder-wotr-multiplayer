using System;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Networking.Messages.Contracts;
using WOTRMultiplayer.Networking.Messages.Game;

internal static partial class Program
{
    private static bool ProbeAcceptedOrderReference()
    {
        try
        {
            // Plain DTOs only: no native entities, rules, commands or world.
            var host = new NetworkRealTimeCombatState
            {
                Mode = CombatMode.RealTimeWithPause, Phase = CombatSynchronizationPhase.Repairing,
                Epoch = 3, LastAuthoritativeSequence = 8
            };
            RealTimeAcceptedOrderLedger.Record(host, new NotifyRealTimeCombatCommandAccepted
            {
                Epoch = 3, AuthoritativeSequence = 5, PlayerId = 42, SessionId = "offline-session",
                LoadedSaveSeed = 4, CommandId = "42:3:1", Movement = new NetworkUnitMoveTo { InitiatorUnitId = "offline-hero" }
            });
            var reference = RealTimeAcceptedOrderLedger.CaptureReference(host, "offline-session", 4);
            var client = new NetworkRealTimeCombatState
            {
                Mode = CombatMode.RealTimeWithPause, Phase = CombatSynchronizationPhase.Repairing, Epoch = 3
            };
            RealTimeAcceptedOrderLedger.RestoreReference(client, reference, "offline-session", 4, 3, 8, () => true);
            var snapshot = RealTimeAcceptedOrderLedger.Snapshot(client);
            var safe = snapshot.Length == 1 && snapshot[0].Payload == host.AcceptedOrders[0].Payload
                && client.LastAuthoritativeSequence == 0 && client.BlocksSaving;
            snapshot[0].UnitId = "mutation";
            safe &= RealTimeAcceptedOrderLedger.Snapshot(client)[0].UnitId == "offline-hero";
            client.Phase = CombatSynchronizationPhase.Stable;
            var rejected = false;
            try { RealTimeAcceptedOrderLedger.RestoreReference(client, reference, "offline-session", 4, 3, 8, () => true); }
            catch (InvalidOperationException) { rejected = true; }
            safe &= rejected;
            var verifiedHash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot
            { ActiveOrders = new System.Collections.Generic.List<RealTimeCommandEnvelope>(RealTimeAcceptedOrderLedger.MaterializeReference(reference)) });
            RealTimeAcceptedOrderLedger.CommitSnapshotReference(client, reference, "offline-session", 4, 3, 8, verifiedHash, () => true);
            var metadataCommit = client.LastAuthoritativeSequence == 8 && client.CanonicalStateHash == verifiedHash
                && client.AcceptedOrders.Count == 1 && client.Phase == CombatSynchronizationPhase.Stable;
            safe &= metadataCommit;
            Console.WriteLine("accepted-order-reference: evidence-restored=" + safe
                + "; cursor-not-advanced=True; snapshot-isolated=True; stable-restoration-rejected=" + rejected
                + "; separate-stable-snapshot-metadata-commit=" + metadataCommit
                + "; native-commands-not-invoked=True");
            return safe;
        }
        catch (Exception error) { Console.WriteLine("accepted-order-reference: FAILED: " + error); return false; }
    }
}
