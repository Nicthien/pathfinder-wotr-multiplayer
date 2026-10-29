using System;
using System.Collections.Generic;
using System.IO;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking
{
    public static class RealTimeAcceptedOrderReferenceValidation
    {
        public const int MaximumOrders = 32;

        public static void RequireValid(NetworkAcceptedOrderReference reference)
        {
            if (reference == null || string.IsNullOrWhiteSpace(reference.SessionId) || reference.SessionId.Length > 128
                || reference.Epoch < 0 || reference.LastIncludedSequence < 0 || reference.Orders == null
                || reference.Orders.Count > MaximumOrders)
                throw new InvalidDataException("native-order-reference-invalid");
            long previous = 0;
            var commands = new HashSet<string>(StringComparer.Ordinal);
            foreach (var order in reference.Orders)
            {
                if (order == null || order.Sequence <= previous || order.Sequence > reference.LastIncludedSequence
                    || string.IsNullOrWhiteSpace(order.UnitId) || order.UnitId.Length > 256
                    || order.Kind < 0 || order.Kind > 4 || string.IsNullOrWhiteSpace(order.CommandId)
                    || order.CommandId.Length > 128 || !commands.Add(order.CommandId)
                    || order.PayloadHash == null || order.PayloadHash.Length != 44)
                    throw new InvalidDataException("native-order-evidence-invalid");
                try
                {
                    var bytes = Convert.FromBase64String(order.PayloadHash);
                    if (bytes.Length != 32 || Convert.ToBase64String(bytes) != order.PayloadHash)
                        throw new FormatException();
                }
                catch (FormatException) { throw new InvalidDataException("native-order-payload-hash-invalid"); }
                previous = order.Sequence;
            }
        }

        public static void RequireIdentity(NetworkAcceptedOrderReference reference, string session, int save, long epoch, long sequence)
        {
            RequireValid(reference);
            if (reference.SessionId != session || reference.LoadedSaveSeed != save || reference.Epoch != epoch
                || reference.LastIncludedSequence != sequence)
                throw new InvalidDataException("native-order-reference-identity-mismatch");
        }
    }
}
