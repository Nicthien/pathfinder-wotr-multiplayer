using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ProtoBuf;
using WOTRMultiplayer.Networking.Messages.Contracts;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Networking.UnitTests
{
    public class RealTimeNativeReferenceCodecTests
    {
        [Test]
        public void ReferenceRoundTripsWithoutAliasingAndKeepsKineticistValue()
        {
            var state = new NetworkCombatState();
            state.Units.Add(new NetworkUnit { Id = "hero", UnitPartKineticist = new NetworkUnitPartKineticist { AcceptedBurn = 5 } });
            state.NativeUnits.Add(new NetworkUnit
            {
                Id = "world-npc",
                BuffCollection = new NetworkUnitBuffCollection()
            });
            var bytes = RealTimeNativeReferenceCodec.Encode(state);
            state.Units[0].UnitPartKineticist.AcceptedBurn = 99;
            state.NativeUnits[0].Id = "mutated";
            var restored = RealTimeNativeReferenceCodec.Decode(bytes);
            Assert.That(restored.Units[0].UnitPartKineticist.AcceptedBurn, Is.EqualTo(5));
            Assert.That(restored.Units[0].Id, Is.EqualTo("hero"));
            Assert.That(restored.NativeUnits.Single().Id, Is.EqualTo("world-npc"));
        }

        [Test]
        public void EmptyReferenceHasExplicitFormatAndCanRoundTrip()
        {
            var bytes = RealTimeNativeReferenceCodec.Encode(new NetworkCombatState());
            Assert.That(bytes.Length, Is.GreaterThanOrEqualTo(8));
            Assert.That(RealTimeNativeReferenceCodec.Decode(bytes).Units, Is.Empty);
        }

        [TestCase(0)]
        [TestCase(7)]
        [TestCase(8)]
        [TestCase(8388609)]
        public void MissingMalformedOrOversizedFormatIsRejected(int length)
        {
            Assert.Throws<InvalidDataException>(() => RealTimeNativeReferenceCodec.Decode(new byte[length]));
        }

        [Test]
        public void OversizedReferenceCannotBeSerializedBeyondLimit()
        {
            var state = new NetworkCombatState();
            state.Units.Add(new NetworkUnit { Id = new string('x', RealTimeNativeReferenceCodec.MaximumBytes + 1) });
            Assert.Catch(() => RealTimeNativeReferenceCodec.Encode(state));
        }

        [Test]
        public void TooManyEntitiesRejectedBeforeSerialization()
        {
            var state = new NetworkCombatState();
            for (var i = 0; i < 4097; i++) state.Units.Add(new NetworkUnit { Id = i.ToString() });
            Assert.Throws<InvalidDataException>(() => RealTimeNativeReferenceCodec.Encode(state));
        }

        [Test]
        public void TooManyNativeUnitInventoriesAreRejectedBeforeSerialization()
        {
            var state = new NetworkCombatState();
            for (var i = 0; i < 32769; i++) state.NativeUnits.Add(new NetworkUnit { Id = i.ToString() });
            Assert.Throws<InvalidDataException>(() => RealTimeNativeReferenceCodec.Encode(state));
        }

        [Test]
        public void VerifyFrameOwnsPayloadAndCannotBeUsedAsOrdinaryManifest()
        {
            var manifest = RealTimeNativeCheckpointTransferTests.Manifest(new byte[] { 1 });
            var verify = RealTimeNativeCheckpointLimits.Copy(manifest);
            verify.Stage = (int)RealTimeNativeCheckpointStage.Verify;
            verify.ReferenceStateBytes = RealTimeNativeReferenceCodec.Encode(new NetworkCombatState());
            verify.ReferenceStateHash = RealTimeNativeCheckpointLimits.Hash(verify.ReferenceStateBytes);
            var copied = RealTimeNativeCheckpointLimits.Copy(Serializer.DeepClone(verify));
            Assert.That(RealTimeNativeCheckpointLimits.IsManifest(verify), Is.False);
            Assert.That(RealTimeNativeCheckpointLimits.ReferenceManifest(copied).ArchiveHash, Is.EqualTo(manifest.ArchiveHash));
            verify.ReferenceStateBytes[0] ^= 1;
            Assert.That(RealTimeNativeCheckpointLimits.ReferenceManifest(verify), Is.Null);
            Assert.That(RealTimeNativeCheckpointLimits.ReferenceManifest(copied), Is.Not.Null);
        }
    }
}
