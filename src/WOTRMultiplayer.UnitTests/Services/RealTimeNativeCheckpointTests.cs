using System;
using System.IO;
using System.Linq;
using System.Text;
using Kingmaker.EntitySystem.Persistence;
using NUnit.Framework;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeNativeCheckpointTests
    {
        private const string Id = "11111111111111111111111111111111";
        private static RealTimeMemorySaver Source(Action<string, byte[]> write = null)
        {
            var source = new RealTimeMemorySaver(writeStash: write);
            source.SaveJson("header", "{\"GameId\":\"campaign\"}");
            source.SaveJson("player", "{\"native-player\":1}");
            source.SaveJson("party", "{\"native-party\":2}");
            return source;
        }

        [Test]
        public void BytesAndFileNamesCannotMutateOwnedSource()
        {
            using var source = Source(); var bytes = new byte[] { 1, 2, 3 };
            source.SaveBytes("area.fog", bytes); bytes[0] = 9;
            var copy = source.ReadBytes("area.fog"); copy[0] = 8;
            var names = source.GetAllFiles(); names.Clear();
            Assert.That(source.ReadBytes("area.fog"), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(source.GetAllFiles().Count, Is.EqualTo(4));
        }

        [Test]
        public void NativeInterfaceCloneSurvivesOriginalDisposal()
        {
            ISaver source = Source(); ((RealTimeMemorySaver)source).Seal();
            using var clone = source.Clone(); source.Dispose();
            Assert.That(clone.ReadHeader(), Does.Contain("campaign"));
            Assert.That(clone.ReadJson("party"), Does.Contain("native-party"));
            Assert.Throws<InvalidOperationException>(() => clone.SaveJson("party", "{}"));
            Assert.Throws<InvalidOperationException>(() => clone.Save());
        }

        [Test]
        public void CopySourceNeverWritesClearsOrDisposesOriginal()
        {
            using var original = Source();
            using var copy = RealTimeMemorySaver.CopySource(original);
            copy.SaveJson("player", "{\"updated\":true}"); copy.Seal();
            Assert.That(original.ReadJson("player"), Does.Contain("native-player"));
            original.SaveJson("still-mutable", "{}");
            Assert.That(copy.ReadJson("still-mutable"), Is.Null);
        }

        [TestCase("../player.json")]
        [TestCase("..\\player.json")]
        [TestCase("C:\\player.json")]
        [TestCase("player.json:stream")]
        [TestCase("NUL")]
        [TestCase("con.json")]
        [TestCase("COM1.txt")]
        [TestCase("LPT¹.json")]
        [TestCase("CON .txt")]
        [TestCase("player.json.")]
        [TestCase("player.json ")]
        [TestCase(" player.json")]
        [TestCase("player\u202e.json")]
        [TestCase(".")]
        [TestCase("..")]
        [TestCase("")]
        public void UnsafeWindowsNamesAreRejectedBeforeStashAccess(string name)
        {
            var called = false;
            using var source = new RealTimeMemorySaver(_ => { called = true; return new byte[0]; });
            Assert.Throws<InvalidDataException>(() => source.CopyFromStash(name));
            Assert.That(called, Is.False);
        }

        [Test]
        public void WindowsAliasesCannotOverwriteSourceEntries()
        {
            using var source = Source();
            Assert.Throws<InvalidDataException>(() => source.SaveJson("PLAYER", "{}"));
            Assert.That(source.ReadJson("player"), Does.Contain("native-player"));
        }

        [Test]
        public void SealedSourceCannotWriteAPlayerSaveOrChangeContents()
        {
            using var source = Source(); source.Seal();
            Assert.Throws<InvalidOperationException>(() => source.Save());
            Assert.Throws<InvalidOperationException>(() => source.Clear());
            Assert.Throws<InvalidOperationException>(() => source.SaveBytes("x", new byte[0]));
            Assert.Throws<InvalidOperationException>(() => source.CopyToStash("player.json"));
        }

        [Test]
        public void StashWriteRequiresSealedSourceAndExplicitCapability()
        {
            byte[] written = null; string file = null;
            using var source = Source((name, bytes) => { file = name; written = bytes; bytes[0] = 0; });
            Assert.Throws<InvalidOperationException>(() => source.CopyToStash("player.json"));
            source.Seal(); source.CopyToStash("player.json");
            Assert.That(file, Is.EqualTo("player.json")); Assert.That(written, Is.Not.Null);
            Assert.That(source.ReadJson("player"), Does.Contain("native-player"));
        }

        [TestCase("header")]
        [TestCase("player")]
        [TestCase("party")]
        public void RequiredNativeDocumentsMustExistBeforeSealing(string missing)
        {
            using var source = new RealTimeMemorySaver();
            foreach (var name in new[] { "header", "player", "party" }.Where(name => name != missing)) source.SaveJson(name, "{}");
            Assert.Throws<InvalidDataException>(() => source.Seal());
            Assert.That(source.IsSealed, Is.False);
        }

        [Test]
        public void BinaryTransferIsDeterministicAndRoundTripsNativeBytes()
        {
            using var source = Source(); source.SaveBytes("area.fog", new byte[] { 4, 5, 6 }); source.Seal();
            var encoded = RealTimeNativeCheckpointCodec.Encode(source);
            using var clone = (RealTimeMemorySaver)source.Clone();
            Assert.That(RealTimeNativeCheckpointCodec.Encode(clone), Is.EqualTo(encoded));
            using var restored = RealTimeNativeCheckpointCodec.Decode(encoded, RealTimeNativeCheckpointCodec.Hash(encoded));
            Assert.That(restored.IsSealed, Is.True); Assert.That(restored.TotalBytes, Is.EqualTo(source.TotalBytes));
            foreach (var name in source.GetAllFiles()) Assert.That(restored.ReadBytes(name), Is.EqualTo(source.ReadBytes(name)));
        }

        [Test]
        public void CorruptedTransferNeverGetsAStashWrite()
        {
            using var source = Source(); source.Seal();
            var bytes = RealTimeNativeCheckpointCodec.Encode(source); var hash = RealTimeNativeCheckpointCodec.Hash(bytes);
            bytes[bytes.Length - 1] ^= 1; var wrote = false;
            Assert.Throws<InvalidDataException>(() => RealTimeNativeCheckpointCodec.Decode(bytes, hash, (_, _) => wrote = true));
            Assert.That(wrote, Is.False);
        }

        [TestCase(-1)]
        [TestCase(RealTimeMemorySaver.MaximumFiles + 1)]
        public void ForgedEntryCountCannotAllocateUnboundedState(int count)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes("RTWNCP01")); writer.Write(count); writer.Flush();
            var bytes = stream.ToArray();
            Assert.Throws<InvalidDataException>(() => RealTimeNativeCheckpointCodec.Decode(bytes, RealTimeNativeCheckpointCodec.Hash(bytes)));
        }

        [Test]
        public void TrailingArchiveBytesAreRefusedEvenWithMatchingHash()
        {
            using var source = Source(); source.Seal();
            var bytes = RealTimeNativeCheckpointCodec.Encode(source).Concat(new byte[] { 1 }).ToArray();
            Assert.Throws<InvalidDataException>(() => RealTimeNativeCheckpointCodec.Decode(bytes, RealTimeNativeCheckpointCodec.Hash(bytes)));
        }

        [Test]
        public void NativeLoadHandleUsesIsolatedSaverWithoutLoadingGame()
        {
            using var source = Source(); source.Seal();
            var info = RealTimeNativeCheckpointLoadSource.Prepare(source, "campaign", Id,
                _ => new SaveInfo { GameId = "campaign" });
            Assert.That(info.IsActuallySaved, Is.True); Assert.That(info.FolderName, Does.StartWith("RTwP-volatile-"));
            source.Dispose();
            using (info.Saver) Assert.That(info.Saver.ReadJson("player"), Does.Contain("native-player"));
        }

        [Test]
        public void NativeHeaderDecoderCanReadSyntheticHeaderWithoutGameOrUnity()
        {
            using var source = Source(); source.Seal();
            var info = RealTimeNativeCheckpointLoadSource.Prepare(source, "campaign", Id);
            using (info.Saver)
            {
                Assert.That(info.GameId, Is.EqualTo("campaign"));
                Assert.That(info.Saver.ReadHeader(), Does.Contain("campaign"));
            }
        }

        [TestCase("{\"GameId\":\"other\"}")]
        [TestCase("{\"GameId\":1}")]
        [TestCase("{\"GameId\":\"campaign\",\"GameId\":\"campaign\"}")]
        public void WrongOrAmbiguousHeaderIsRefusedBeforeNativeDecode(string header)
        {
            using var source = Source(); source.SaveJson("header", header); source.Seal(); var decoded = false;
            Assert.That(() => RealTimeNativeCheckpointLoadSource.Prepare(source, "campaign", Id,
                _ => { decoded = true; return new SaveInfo(); }), Throws.Exception);
            Assert.That(decoded, Is.False);
        }

        [TestCase("not-json")]
        [TestCase("{} {}")]
        [TestCase("{\"fact\":1,\"fact\":2}")]
        public void BrokenMechanicalJsonIsRefusedBeforeNativeDecode(string json)
        {
            using var source = Source(); source.SaveJson("party", json); source.Seal(); var decoded = false;
            Assert.That(() => RealTimeNativeCheckpointLoadSource.Prepare(source, "campaign", Id,
                _ => { decoded = true; return new SaveInfo(); }), Throws.Exception);
            Assert.That(decoded, Is.False);
        }

        [Test]
        public void OptionalNativeDocumentsPreserveArraysAndEmptyTurnBasedData()
        {
            using var source = Source(); source.SaveJson("tbm", ""); source.SaveJson("extra", "[1,{\"x\":2}]"); source.Seal();
            var info = RealTimeNativeCheckpointLoadSource.Prepare(source, "campaign", Id, _ => new SaveInfo { GameId = "campaign" });
            using (info.Saver)
            {
                Assert.That(info.Saver.ReadJson("tbm"), Is.EqualTo(""));
                Assert.That(info.Saver.ReadJson("extra"), Does.StartWith("["));
            }
        }

        [TestCase("null")]
        [TestCase("[]")]
        [TestCase("1")]
        [TestCase("\"text\"")]
        public void RequiredMechanicalDocumentsCannotUseScalarOrArrayRoots(string json)
        {
            using var source = Source(); source.SaveJson("player", json); source.Seal();
            Assert.Throws<InvalidDataException>(() => RealTimeNativeCheckpointLoadSource.Prepare(source, "campaign", Id,
                _ => { Assert.Fail("must not deserialize"); return null; }));
        }

        [Test]
        public void InvalidNativeDecodedHeaderCannotAttachAnUnrelatedSaver()
        {
            using var source = Source(); source.Seal();
            Assert.Throws<InvalidDataException>(() => RealTimeNativeCheckpointLoadSource.Prepare(source, "campaign", Id,
                _ => new SaveInfo { GameId = "another" }));
            Assert.Throws<InvalidDataException>(() => RealTimeNativeCheckpointLoadSource.Prepare(source, "campaign", Id,
                _ => new SaveInfo { GameId = "campaign", Saver = source }));
            Assert.That(source.ReadHeader(), Does.Contain("campaign"));
        }

        [TestCase(0)]
        [TestCase(7)]
        [TestCase(8)]
        [TestCase(11)]
        public void TruncatedArchiveIsRejectedWithAUsefulDiagnostic(int length)
        {
            var bytes = Encoding.ASCII.GetBytes("RTWNCP01xxxx").Take(length).ToArray();
            Assert.Throws<InvalidDataException>(() => RealTimeNativeCheckpointCodec.Decode(bytes, RealTimeNativeCheckpointCodec.Hash(bytes)));
        }

        [Test]
        public void Utf8BomIsSupportedButCorruptedTextIsNotReplaced()
        {
            using var source = Source();
            source.SaveBytes("party.json", new byte[] { 0xef, 0xbb, 0xbf, (byte)'{', (byte)'}' });
            Assert.That(source.ReadJson("party"), Is.EqualTo("{}"));
            source.SaveBytes("party.json", new byte[] { 0xff });
            Assert.Throws<DecoderFallbackException>(() => source.ReadJson("party"));
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(801)]
        public void ForgedNameLengthIsRejectedBeforeAllocatingOrReadingIt(int length)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes("RTWNCP01")); writer.Write(3); writer.Write(length); writer.Flush();
            var bytes = stream.ToArray();
            Assert.Throws<InvalidDataException>(() => RealTimeNativeCheckpointCodec.Decode(bytes, RealTimeNativeCheckpointCodec.Hash(bytes)));
        }

        [TestCase(-1)]
        [TestCase(RealTimeMemorySaver.MaximumFileBytes + 1)]
        public void ForgedFileLengthIsRejectedWithoutLargeAllocations(int length)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes("RTWNCP01")); writer.Write(3);
            var name = Encoding.UTF8.GetBytes("header.json"); writer.Write(name.Length); writer.Write(name); writer.Write(length); writer.Flush();
            var bytes = stream.ToArray();
            Assert.Throws<InvalidDataException>(() => RealTimeNativeCheckpointCodec.Decode(bytes, RealTimeNativeCheckpointCodec.Hash(bytes)));
        }

        [TestCase("")]
        [TestCase("not-an-id")]
        [TestCase("11111111-1111-1111-1111-111111111111")]
        public void InvalidCheckpointIdentityNeverCallsNativeDecoder(string id)
        {
            using var source = Source(); source.Seal(); var decoded = false;
            Assert.Throws<InvalidOperationException>(() => RealTimeNativeCheckpointLoadSource.Prepare(source, "campaign", id,
                _ => { decoded = true; return null; }));
            Assert.That(decoded, Is.False);
        }

        [Test]
        public void FailedStashWriteDoesNotCorruptSourceOrOtherClones()
        {
            using var source = Source((_, _) => throw new IOException("injected")); source.Seal();
            using var clone = source.Clone();
            Assert.Throws<IOException>(() => clone.CopyToStash("player.json"));
            Assert.That(source.ReadJson("player"), Does.Contain("native-player"));
            Assert.That(clone.ReadJson("player"), Does.Contain("native-player"));
        }

        [Test]
        public void AreaJsonMustBeAnObjectBeforeWorldDestructionIsPossible()
        {
            using var source = Source(); source.SaveJson(Id, "null"); source.Seal();
            Assert.Throws<InvalidDataException>(() => RealTimeNativeCheckpointLoadSource.Prepare(source, "campaign", Id,
                _ => { Assert.Fail("must not decode"); return null; }));
        }
    }
}
