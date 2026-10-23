using System;
using System.IO;
using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Services.CombatSaving;
using WOTRMultiplayer.Services.GameInteraction;
using WOTRMultiplayer.Services.Random;

namespace WOTRMultiplayer.UnitTests
{
    public class CombatSaveArchiveTests
    {
        private string _directory;
        [SetUp] public void SetUp() => _directory = Directory.CreateDirectory(Path.Combine(
            TestContext.CurrentContext.WorkDirectory, "combat-save-tests", Guid.NewGuid().ToString("N"))).FullName;
        private static CombatSaveManifest Manifest() => new()
        {
            Protocol = 70, BuildFingerprint = new string('A', 64), CanonicalDigest = new string('B', 64),
            RestoreDigest = new string('C', 64),
            GameVersion = "test", Campaign = "campaign", Unit = "unit", Round = 2,
            MechanicalState = new NetworkCombatState(),
            NativeSupplement = CombatSaveNativeContractTests.Supplement(),
            Generators = new DeterministicGeneratorCheckpoint()
        };
        private static RealTimeMemorySaver Source(string tbm = "{\"RoundNumber\":2}")
        {
            var source = new RealTimeMemorySaver();
            source.SaveJson("header", "{\"GameId\":\"campaign\"}");
            source.SaveJson("player", "{}"); source.SaveJson("party", "[]"); source.SaveJson("tbm", tbm);
            source.Seal(); return source;
        }
        [Test]
        public void NewSaveRoundTripIncludesMetadataWithoutOverwriting()
        {
            using var source = Source();
            var manifest = Manifest();
            var first = CombatSaveArchive.WriteNew(_directory, source, manifest, new DateTime(2026, 10, 17));
            var second = CombatSaveArchive.WriteNew(_directory, source, manifest, new DateTime(2026, 10, 17));
            Assert.That(first, Is.Not.EqualTo(second));
            Assert.That(File.Exists(first), Is.True);
            Assert.That(CombatSaveArchive.ValidateFile(first, 70, manifest.BuildFingerprint, "test").Round, Is.EqualTo(2));
            Assert.That(manifest.Files, Is.Empty);
            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
        }
        [Test]
        public void NativeRoundSupplementSurvivesArchiveWithoutChangingSealedSource()
        {
            using var source = Source("{\"m_CurrentTurn\":{\"Status\":\"Acting\"}}");
            var manifest = Manifest();
            manifest.NativeSupplement = CombatSaveNativeContractTests.Supplement();
            manifest.NativeSupplement.TimeSinceStart = 4.5f; manifest.NativeSupplement.TimeToNextRound = 1.5f;
            var path = CombatSaveArchive.WriteNew(_directory, source, manifest, DateTime.Now);
            CombatSaveArchive.ValidateFile(path, 70, manifest.BuildFingerprint, "test");
            var native = Newtonsoft.Json.Linq.JObject.Parse(CombatSaveArchive.ReadNativeTurn(path));
            var restored = CombatSaveNativeSupplement.Read(native);
            Assert.That(restored.RoundNumber, Is.EqualTo(2));
            Assert.That(restored.TimeSinceStart, Is.EqualTo(4.5f));
            Assert.That(restored.TimeToNextRound, Is.EqualTo(1.5f));
            Assert.That((string)native["m_CurrentTurn"]["Status"], Is.EqualTo("Acting"));
            Assert.That(System.Text.Encoding.UTF8.GetString(source.ReadBytes("tbm.json")), Does.Not.Contain("RoundNumber"));
        }

        [TestCase("")]
        [TestCase("{}")]
        [TestCase("null")]
        [TestCase("{\"RoundNumber\":2,\"RoundNumber\":3}")]
        public void MissingOrAmbiguousTurnStatePublishesNothing(string tbm)
        {
            using var source = Source(tbm);
            Assert.Catch(() => CombatSaveArchive.WriteNew(_directory, source, Manifest(), DateTime.Now));
            Assert.That(Directory.GetFiles(_directory), Is.Empty);
        }
        [Test]
        public void DifferentBuildRejectedBeforeNativeLoad()
        {
            using var source = Source();
            var file = CombatSaveArchive.WriteNew(_directory, source, Manifest(), DateTime.Now);
            Assert.Throws<InvalidDataException>(() => CombatSaveArchive.ValidateFile(file, 71, new string('A', 64), "test"));
            Assert.Throws<InvalidDataException>(() => CombatSaveArchive.ValidateFile(file, 70, new string('C', 64), "test"));
            Assert.Throws<InvalidDataException>(() => CombatSaveArchive.ValidateFile(file, 70, new string('A', 64), "other"));
        }

        [Test]
        public void SchemaTwoRequiresMechanicalStateWhileLegacySchemaRemainsReadable()
        {
            using var source = Source();
            var current = Manifest();
            current.MechanicalState = null;
            Assert.Throws<InvalidDataException>(() =>
                CombatSaveArchive.WriteNew(_directory, source, current, DateTime.Now));

            var legacy = Manifest();
            legacy.Schema = 1;
            legacy.MechanicalState = null;
            var file = CombatSaveArchive.WriteNew(_directory, source, legacy, DateTime.Now);
            Assert.That(CombatSaveArchive.ValidateFile(file, 70, legacy.BuildFingerprint, "test").Schema,
                Is.EqualTo(1));
        }

        [Test]
        public void ExplicitCompatibleBuildIsAcceptedWithoutWeakeningDefaultValidation()
        {
            using var source = Source();
            var file = CombatSaveArchive.WriteNew(_directory, source, Manifest(), DateTime.Now);
            var current = new string('C', 64);

            Assert.Throws<InvalidDataException>(() =>
                CombatSaveArchive.ValidateFile(file, 70, current, "test"));
            Assert.That(CombatSaveArchive.ValidateFile(file, 70, current, "test",
                new[] { new string('A', 64) }).BuildFingerprint, Is.EqualTo(new string('A', 64)));
            Assert.Throws<InvalidDataException>(() => CombatSaveArchive.ValidateFile(file, 71, current, "test",
                new[] { new string('A', 64) }));
            Assert.Throws<InvalidDataException>(() => CombatSaveArchive.ValidateFile(file, 70, current, "other",
                new[] { new string('A', 64) }));
        }

        [Test]
        public void LastValidatedCombatSaveBuildRemainsAnExplicitMigrationInput()
        {
            using var source = Source();
            var manifest = Manifest();
            manifest.BuildFingerprint = "2EB06FB0790AFBB70FCC423886651C2DD3D03E1F75B588F9E101E5F90206E0EA";
            var file = CombatSaveArchive.WriteNew(_directory, source, manifest, DateTime.Now);

            Assert.That(CombatSaveArchive.ValidateFile(file, 70, new string('D', 64), "test",
                    CombatSaveCompatibility.AcceptedArchiveFingerprints).BuildFingerprint,
                Is.EqualTo(manifest.BuildFingerprint));
        }

        [Test]
        public void EngagementFixAcceptsTheExactFailedVersion96ArchiveForRetest()
        {
            using var source = Source();
            var manifest = Manifest();
            manifest.BuildFingerprint = "6CC97D808D54FA135AE5715C5CC7DE4FA2EAD62BBBBFAC0E6E5C2051781BA138";
            var file = CombatSaveArchive.WriteNew(_directory, source, manifest, DateTime.Now);

            Assert.That(CombatSaveArchive.ValidateFile(file, 70, new string('D', 64), "test",
                    CombatSaveCompatibility.AcceptedArchiveFingerprints).BuildFingerprint,
                Is.EqualTo(manifest.BuildFingerprint));
        }

        [Test]
        public void NativeHeightFixAcceptsTheExactFailedVersion97ArchiveForRetest()
        {
            using var source = Source();
            var manifest = Manifest();
            manifest.BuildFingerprint = "E69E7A4D5D6958C9EAA1D0D5413853F76585E6A515E924B038371B6BD9262613";
            var file = CombatSaveArchive.WriteNew(_directory, source, manifest, DateTime.Now);

            Assert.That(CombatSaveArchive.ValidateFile(file, 70, new string('D', 64), "test",
                    CombatSaveCompatibility.AcceptedArchiveFingerprints).BuildFingerprint,
                Is.EqualTo(manifest.BuildFingerprint));
        }
    }
}
