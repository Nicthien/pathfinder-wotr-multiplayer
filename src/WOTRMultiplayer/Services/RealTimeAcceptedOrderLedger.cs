using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ProtoBuf;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Services
{
    /// <summary>Bounded fingerprint of host-accepted orders, shared by both peers in one combat epoch.</summary>
    public static class RealTimeAcceptedOrderLedger
    {
        private const int RetainedOrders = 32;

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
                    Payload = payload
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
                return state.AcceptedOrders?.Where(order => order.Epoch == state.Epoch).ToArray() ?? [];
        }
    }
}
