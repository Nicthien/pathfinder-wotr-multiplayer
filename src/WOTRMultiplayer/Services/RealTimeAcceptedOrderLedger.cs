using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ProtoBuf;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Services
{
    /// <summary>Bounded fingerprint of host-accepted orders, shared by both peers in one combat epoch.</summary>
    public static class RealTimeAcceptedOrderLedger
    {
        private const int RetainedOrders = RealTimeAcceptedOrderReferenceValidation.MaximumOrders;

        public static void Record(NetworkRealTimeCombatState state, NotifyRealTimeCombatCommandAccepted accepted)
        {
            if (state == null || accepted == null
                || accepted.AuthoritativeSequence <= 0)
            {
                return;
            }
            var unitId = accepted.Movement?.InitiatorUnitId
                ?? accepted.Attack?.InitiatorUnitId
                ?? accepted.Ability?.InitiatorUnitId;
            if (string.IsNullOrWhiteSpace(unitId))
            {
                return;
            }

            using var bytes = new MemoryStream();
            Serializer.Serialize(bytes, accepted);
            using var sha = SHA256.Create();
            var payload = Convert.ToBase64String(sha.ComputeHash(bytes.ToArray()));
            lock (state)
            {
                if (accepted.Epoch != state.Epoch) return;
                state.AcceptedOrders.RemoveAll(order => order.Epoch != state.Epoch);
                if (state.AcceptedOrders.Any(order => order.Sequence == accepted.AuthoritativeSequence))
                    return;
                state.AcceptedOrders.Add(new RealTimeCommandEnvelope
                {
                    Epoch = accepted.Epoch,
                    Sequence = accepted.AuthoritativeSequence,
                    PlayerId = accepted.PlayerId,
                    UnitId = unitId,
                    Kind = (RealTimeCommandKind)accepted.Kind,
                    Payload = payload,
                    SessionId = accepted.SessionId,
                    LoadedSaveSeed = accepted.LoadedSaveSeed,
                    CommandId = accepted.CommandId
                });
                state.AcceptedOrders.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
                if (state.AcceptedOrders.Count > RetainedOrders)
                    state.AcceptedOrders.RemoveRange(0, state.AcceptedOrders.Count - RetainedOrders);
            }
        }

        public static RealTimeCommandEnvelope[] Snapshot(NetworkRealTimeCombatState state)
        {
            if (state == null) return [];
            lock (state)
                return state.AcceptedOrders?.Where(order => order.Epoch == state.Epoch).Select(Clone).ToArray() ?? [];
        }

        private static RealTimeCommandEnvelope Clone(RealTimeCommandEnvelope order) => new()
        {
            Epoch = order.Epoch, Sequence = order.Sequence, PlayerId = order.PlayerId,
            UnitId = order.UnitId, Kind = order.Kind, Payload = order.Payload,
            SessionId = order.SessionId, LoadedSaveSeed = order.LoadedSaveSeed,
            CommandId = order.CommandId, ObservedStateHash = order.ObservedStateHash
        };

        public static NetworkAcceptedOrderReference CaptureReference(NetworkRealTimeCombatState state, string session, int save)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            lock (state)
            {
                var orders = Snapshot(state);
                if (orders.Any(order => order.SessionId != session || order.LoadedSaveSeed != save))
                    throw new InvalidOperationException("native-order-ledger-session-mismatch");
                var result = new NetworkAcceptedOrderReference
                {
                    SessionId = session, LoadedSaveSeed = save, Epoch = state.Epoch,
                    LastIncludedSequence = state.LastAuthoritativeSequence,
                    Orders = orders.Select(order => new NetworkAcceptedOrderEvidence
                    {
                        Sequence = order.Sequence, PlayerId = order.PlayerId, UnitId = order.UnitId,
                        Kind = (int)order.Kind, PayloadHash = order.Payload, CommandId = order.CommandId
                    }).ToList()
                };
                RealTimeAcceptedOrderReferenceValidation.RequireValid(result);
                return result;
            }
        }

        public static void RequireUnchanged(NetworkRealTimeCombatState state, NetworkAcceptedOrderReference expected,
            string session, int save)
        {
            var actual = CaptureReference(state, session, save);
            RealTimeAcceptedOrderReferenceValidation.RequireIdentity(expected, session, save, actual.Epoch, actual.LastIncludedSequence);
            using var left = new MemoryStream(); using var right = new MemoryStream();
            Serializer.Serialize(left, expected); Serializer.Serialize(right, actual);
            if (!left.ToArray().SequenceEqual(right.ToArray()))
                throw new InvalidOperationException("native-order-ledger-changed");
        }

        public static RealTimeCommandEnvelope[] MaterializeReference(NetworkAcceptedOrderReference reference)
        {
            RealTimeAcceptedOrderReferenceValidation.RequireValid(reference);
            var copy = Serializer.DeepClone(reference);
            RealTimeAcceptedOrderReferenceValidation.RequireValid(copy);
            return copy.Orders.Select(order => new RealTimeCommandEnvelope
            {
                Epoch = copy.Epoch, Sequence = order.Sequence, PlayerId = order.PlayerId, UnitId = order.UnitId,
                Kind = (RealTimeCommandKind)order.Kind, Payload = order.PayloadHash, CommandId = order.CommandId,
                SessionId = copy.SessionId, LoadedSaveSeed = copy.LoadedSaveSeed
            }).ToArray();
        }

        // Caller must first verify actual combat data/hash under the snapshot gate.
        // Metadata/cursor commit only: no gameplay, native graph or resume permission.
        public static void CommitSnapshotReference(NetworkRealTimeCombatState state, NetworkAcceptedOrderReference reference,
            string session, int save, long epoch, long includedSequence, ulong verifiedHash, Func<bool> authorized)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (authorized == null) throw new ArgumentNullException(nameof(authorized));
            RealTimeAcceptedOrderReferenceValidation.RequireIdentity(reference, session, save, epoch, includedSequence);
            var copy = Serializer.DeepClone(reference);
            RealTimeAcceptedOrderReferenceValidation.RequireIdentity(copy, session, save, epoch, includedSequence);
            var replacement = MaterializeReference(copy).ToList();
            lock (state)
            {
                if (!authorized() || state.Mode != CombatMode.RealTimeWithPause || state.Phase != CombatSynchronizationPhase.Stable
                    || state.CombatEndPending || state.Epoch != epoch || state.LastAuthoritativeSequence >= includedSequence)
                    throw new InvalidOperationException("snapshot-order-commit-revoked");
                state.AcceptedOrders = replacement;
                state.LastAuthoritativeSequence = includedSequence;
                state.CanonicalStateHash = verifiedHash;
            }
        }

        // Restores bounded evidence only, never native commands, command-gate watermarks,
        // event cursors, combat phase or permission to resume/save. Commit owns those.
        public static void RestoreReference(NetworkRealTimeCombatState state, NetworkAcceptedOrderReference reference,
            string session, int save, long epoch, long includedSequence, Func<bool> authorized)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (authorized == null) throw new ArgumentNullException(nameof(authorized));
            RealTimeAcceptedOrderReferenceValidation.RequireIdentity(reference, session, save, epoch, includedSequence);
            var copy = Serializer.DeepClone(reference ?? throw new ArgumentNullException(nameof(reference)));
            RealTimeAcceptedOrderReferenceValidation.RequireIdentity(copy, session, save, epoch, includedSequence);
            var replacement = copy.Orders.Select(order => new RealTimeCommandEnvelope
            {
                Epoch = epoch, Sequence = order.Sequence, PlayerId = order.PlayerId, UnitId = order.UnitId,
                Kind = (RealTimeCommandKind)order.Kind, Payload = order.PayloadHash, CommandId = order.CommandId,
                SessionId = session, LoadedSaveSeed = save
            }).ToList();
            lock (state)
            {
                if (!authorized() || state.Mode != CombatMode.RealTimeWithPause || state.Phase == CombatSynchronizationPhase.Stable
                    || state.Epoch != epoch || state.LastAuthoritativeSequence > includedSequence)
                    throw new InvalidOperationException("native-order-restoration-barrier-invalid");
                state.AcceptedOrders = replacement;
            }
        }
    }
}
