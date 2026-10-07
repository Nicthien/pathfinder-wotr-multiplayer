using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeNativeHostCaptureTests
    {
        private const string Area = "11111111111111111111111111111111";
        private static void Serialize(RealTimeMemorySaver source)
        {
            source.SaveJson("header", "{\"GameId\":\"synthetic-campaign\"}");
            source.SaveJson("player", "{}"); source.SaveJson("party", "{}");
        }

        [Test]
        public void SuccessfulCaptureRestoresBeforeVerificationAndPublication()
        {
            var trace = new List<string>();
            using var source = RealTimeNativeCaptureTransaction.Capture(transaction =>
            {
                transaction.Apply(() => trace.Add("area-off"), () => trace.Add("area-on"));
                transaction.Apply(() => trace.Add("player-off"), () => trace.Add("player-on"));
            }, saver => { trace.Add("serialize"); Serialize(saver); }, () => { trace.Add("verify"); return true; });
            Assert.That(trace, Is.EqualTo(new[] { "area-off", "player-off", "serialize", "player-on", "area-on", "verify" }));
            Assert.That(source.IsSealed, Is.True);
            Assert.Throws<InvalidOperationException>(() => source.SaveJson("player", "{}"));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void PreparationFailureAfterMutationStillRestoresFailingStepAndEarlierSteps(int failAt)
        {
            var state = new bool[3]; var restorations = new List<int>(); var serialized = false;
            Assert.Throws<InvalidOperationException>(() => RealTimeNativeCaptureTransaction.Capture(transaction =>
            {
                for (var i = 0; i < 3; i++)
                {
                    var step = i;
                    transaction.Apply(() => { state[step] = true; if (step == failAt) throw new InvalidOperationException("partial-prepare"); },
                        () => { state[step] = false; restorations.Add(step); });
                }
            }, _ => serialized = true, () => true));
            Assert.That(state, Is.All.False);
            Assert.That(restorations, Is.EqualTo(Enumerable.Range(0, failAt + 1).Reverse()));
            Assert.That(serialized, Is.False);
        }

        [Test]
        public void SerializationFailureRestoresAndDisposesUnpublishedSource()
        {
            var restored = false; RealTimeMemorySaver captured = null;
            Assert.Throws<IOException>(() => RealTimeNativeCaptureTransaction.Capture(
                transaction => transaction.Apply(() => { }, () => restored = true),
                source => { captured = source; Serialize(source); throw new IOException("serialize"); }, () => true));
            Assert.That(restored, Is.True);
            Assert.Throws<ObjectDisposedException>(() => captured.ReadHeader());
        }

        [Test]
        public void EveryRestorationRunsEvenWhenSeveralFailAndBothCausesAreKept()
        {
            var trace = new List<int>();
            var error = Assert.Throws<AggregateException>(() => RealTimeNativeCaptureTransaction.Capture(transaction =>
            {
                for (var i = 0; i < 3; i++)
                {
                    var step = i;
                    transaction.Apply(() => { }, () => { trace.Add(step); if (step != 1) throw new IOException("restore-" + step); });
                }
            }, _ => throw new InvalidOperationException("serialize"), () => true));
            Assert.That(trace, Is.EqualTo(new[] { 2, 1, 0 }));
            Assert.That(error.Flatten().InnerExceptions.Select(ex => ex.Message),
                Is.EquivalentTo(new[] { "serialize", "restore-2", "restore-0" }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedOrThrowingPostRestoreVerificationDiscardsSource(bool throws)
        {
            RealTimeMemorySaver captured = null; var restored = false;
            Assert.Throws<InvalidOperationException>(() => RealTimeNativeCaptureTransaction.Capture(
                transaction => transaction.Apply(() => { }, () => restored = true),
                source => { captured = source; Serialize(source); },
                () => { Assert.That(restored, Is.True); if (throws) throw new InvalidOperationException("verify"); return false; }));
            Assert.Throws<ObjectDisposedException>(() => captured.GetAllFiles());
        }

        [Test]
        public void MissingRequiredNativeDocumentsAreNeverPublished()
        {
            var restored = false;
            Assert.Throws<InvalidDataException>(() => RealTimeNativeCaptureTransaction.Capture(
                transaction => transaction.Apply(() => { }, () => restored = true), source => source.SaveJson("header", "{}"), () => true));
            Assert.That(restored, Is.True);
        }

        [Test]
        public void TransactionIsSingleUseAndRestorationCannotRegisterAnotherStep()
        {
            var calls = 0;
            var transaction = new RealTimeNativeCaptureTransaction();
            transaction.Apply(() => { }, () =>
            {
                calls++;
                Assert.Throws<ObjectDisposedException>(() => transaction.Apply(() => { }, () => { }));
            });
            transaction.Dispose(); transaction.Dispose();
            Assert.That(calls, Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(() => transaction.Apply(() => { }, () => { }));
        }

        [Test]
        public void NativeAreaNamesUseNoSeparatorAndExactlySixtyTwoSceneCharacters()
        {
            Assert.That(RealTimeNativeCheckpointFiles.AreaJson(Area, ""), Is.EqualTo(Area + ".json"));
            Assert.That(RealTimeNativeCheckpointFiles.AreaJson(Area, "Scene"), Is.EqualTo(Area + "Scene.json"));
            Assert.That(RealTimeNativeCheckpointFiles.AreaJson(Area, new string('x', 80)), Is.EqualTo(Area + new string('x', 62) + ".json"));
            Assert.That(RealTimeNativeCheckpointFiles.Fog(Area, "Scene"), Is.EqualTo(Area + ".Scene.fog"));
        }

        [TestCase("../scene")]
        [TestCase("C:\\scene")]
        [TestCase("scene:stream")]
        public void UnsafeSceneNamesAreRejectedBeforeStashAccess(string scene)
        {
            Assert.Throws<InvalidDataException>(() => RealTimeNativeCheckpointFiles.AreaJson(Area, scene));
            Assert.Throws<InvalidDataException>(() => RealTimeNativeCheckpointFiles.Fog(Area, scene));
        }

        [TestCase("")]
        [TestCase("scene.part")]
        public void NativeFogLoaderAmbiguityIsRejected(string scene)
        { Assert.Throws<InvalidDataException>(() => RealTimeNativeCheckpointFiles.Fog(Area, scene)); }

        [TestCase("invalid")]
        [TestCase("11111111-1111-1111-1111-111111111111")]
        public void NonNativeAreaIdentifiersAreRejected(string id)
        { Assert.Throws<InvalidDataException>(() => RealTimeNativeCheckpointFiles.AreaJson(id, "")); }

        private sealed class ChangedLengthStream : MemoryStream
        {
            private readonly long _reported;
            public ChangedLengthStream(byte[] bytes, long reported) : base(bytes) { _reported = reported; }
            public override long Length => _reported;
        }

        [Test]
        public void ReadBoundedOwnsBytesAndHonorsCurrentPosition()
        {
            var bytes = new byte[] { 1, 2, 3 };
            using var stream = new MemoryStream(bytes); stream.Position = 1;
            var result = RealTimeNativeCheckpointFiles.ReadBounded(stream); bytes[1] = 9;
            Assert.That(result, Is.EqualTo(new byte[] { 2, 3 }));
        }

        [TestCase(-1)]
        [TestCase(67108865)]
        public void StashSizeIsCheckedBeforeAllocation(long length)
        {
            using var stream = new ChangedLengthStream(new byte[0], length);
            Assert.Throws<InvalidDataException>(() => RealTimeNativeCheckpointFiles.ReadBounded(stream));
        }

        [Test]
        public void TruncatedAndGrowingStashAreRejected()
        {
            using var truncated = new ChangedLengthStream(new byte[] { 1 }, 2);
            Assert.Throws<EndOfStreamException>(() => RealTimeNativeCheckpointFiles.ReadBounded(truncated));
            using var growing = new ChangedLengthStream(new byte[] { 1, 2 }, 1);
            Assert.Throws<InvalidDataException>(() => RealTimeNativeCheckpointFiles.ReadBounded(growing));
        }

        [Test]
        public void SyntheticCapturedSourceRoundTripsThroughArchiveAndNativeHeaderDecoder()
        {
            using var source = RealTimeNativeCaptureTransaction.Capture(_ => { }, Serialize, () => true);
            var archive = RealTimeNativeCheckpointCodec.Encode(source);
            using var decoded = RealTimeNativeCheckpointCodec.Decode(archive, RealTimeNativeCheckpointCodec.Hash(archive));
            var header = RealTimeNativeCheckpointLoadSource.Prepare(decoded, "synthetic-campaign", Area);
            using (header.Saver) Assert.That(header.GameId, Is.EqualTo("synthetic-campaign"));
            // No Game.Instance, native player/area, Unity initialization or loader.
        }
    }
}
