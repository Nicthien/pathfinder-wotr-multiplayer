using System;
using System.IO;
using NUnit.Framework;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking.UnitTests
{
    [TestFixture]
    public class RealTimeAcceptedOrderReferenceTests
    {
        private static NetworkAcceptedOrderReference Reference() => new()
        {
            SessionId = "session", LoadedSaveSeed = 4, Epoch = 5, LastIncludedSequence = 9,
            Orders = new() { new() { Sequence = 3, PlayerId = 42, UnitId = "hero", Kind = 2,
                CommandId = "42:5:1", PayloadHash = Convert.ToBase64String(new byte[32]) } }
        };

        [Test]
        public void CodecBindsAndOwnsTheReferenceIncludingEmptyJournal()
        {
            var original = Reference();
            var decoded = RealTimeNativeReferenceCodec.Decode(RealTimeNativeReferenceCodec.Encode(
                new NetworkCombatState { AcceptedOrderReference = original })).AcceptedOrderReference;
            RealTimeAcceptedOrderReferenceValidation.RequireIdentity(decoded, "session", 4, 5, 9);
            original.Orders[0].UnitId = "mutated";
            Assert.That(decoded.Orders[0].UnitId, Is.EqualTo("hero"));
            decoded.Orders.Clear();
            RealTimeAcceptedOrderReferenceValidation.RequireValid(decoded);
        }

        [Test]
        public void FrequentSnapshotCarriesIndependentAcceptedEvidenceInFieldTen()
        {
            var original = new WOTRMultiplayer.Networking.Messages.Game.NotifyRealTimeCombatSnapshot { Sequence = 9, AcceptedOrders = Reference() };
            var copy = ProtoBuf.Serializer.DeepClone(original);
            original.AcceptedOrders.Orders[0].UnitId = "mutated";
            Assert.That(copy.AcceptedOrders.Orders[0].UnitId, Is.EqualTo("hero"));
            RealTimeAcceptedOrderReferenceValidation.RequireIdentity(copy.AcceptedOrders, "session", 4, 5, 9);
        }

        [TestCase("session")][TestCase("save")][TestCase("epoch")][TestCase("sequence")]
        public void OtherBarrierIdentityIsRejected(string fault)
        {
            var reference = Reference();
            Assert.Throws<InvalidDataException>(() => RealTimeAcceptedOrderReferenceValidation.RequireIdentity(reference,
                fault == "session" ? "other" : "session", fault == "save" ? 6 : 4,
                fault == "epoch" ? 6 : 5, fault == "sequence" ? 10 : 9));
        }

        [TestCase("null")][TestCase("session")][TestCase("session-size")][TestCase("epoch")]
        [TestCase("included")][TestCase("orders-null")][TestCase("orders-size")][TestCase("order-null")]
        [TestCase("zero-sequence")][TestCase("future")][TestCase("duplicate-sequence")][TestCase("unordered")]
        [TestCase("unit")][TestCase("unit-size")][TestCase("kind")][TestCase("command")][TestCase("command-size")]
        [TestCase("duplicate-command")][TestCase("hash-null")][TestCase("hash-size")][TestCase("hash-format")]
        [TestCase("hash-noncanonical")]
        public void MalformedJournalCannotBeEncoded(string fault)
        {
            var reference = Reference();
            var order = reference.Orders[0];
            switch (fault)
            {
                case "null": reference = null; break;
                case "session": reference.SessionId = " "; break;
                case "session-size": reference.SessionId = new string('s', 129); break;
                case "epoch": reference.Epoch = -1; break;
                case "included": reference.LastIncludedSequence = -1; break;
                case "orders-null": reference.Orders = null; break;
                case "orders-size": for (var i = 0; i < 32; i++) reference.Orders.Add(order); break;
                case "order-null": reference.Orders[0] = null; break;
                case "zero-sequence": order.Sequence = 0; break;
                case "future": order.Sequence = 10; break;
                case "duplicate-sequence": reference.Orders.Add(order); break;
                case "unordered": reference.Orders.Add(new() { Sequence = 2 }); break;
                case "unit": order.UnitId = " "; break;
                case "unit-size": order.UnitId = new string('u', 257); break;
                case "kind": order.Kind = 5; break;
                case "command": order.CommandId = " "; break;
                case "command-size": order.CommandId = new string('c', 129); break;
                case "duplicate-command": var duplicate = ProtoBuf.Serializer.DeepClone(order); duplicate.Sequence++; reference.Orders.Add(duplicate); break;
                case "hash-null": order.PayloadHash = null; break;
                case "hash-size": order.PayloadHash = "AA=="; break;
                case "hash-format": order.PayloadHash = new string('!', 44); break;
                case "hash-noncanonical": order.PayloadHash = new string('A', 42) + "B="; break;
            }
            Assert.Throws<InvalidDataException>(() => RealTimeAcceptedOrderReferenceValidation.RequireValid(reference));
            if (reference != null)
                Assert.Throws<InvalidDataException>(() => RealTimeNativeReferenceCodec.Encode(new NetworkCombatState
                { AcceptedOrderReference = reference }));
        }
    }
}
