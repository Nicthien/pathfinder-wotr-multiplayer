using System;
using System.IO;
using NUnit.Framework;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.Automation;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class TestControlBridgePolicyTests
    {
        private string _profile;

        [SetUp]
        public void SetUp()
        {
            _profile = Path.Combine(Path.GetTempPath(), "wotr-mcp-policy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_profile);
            File.WriteAllText(Path.Combine(_profile, RealTimeTestProfileBootstrap.MarkerFile), "test");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_profile)) Directory.Delete(_profile, true);
        }

        [Test]
        public void ActivationRequiresMarkedIsolatedProfileAndStrongSessionToken()
        {
            var accepted = TestControlBridgePolicy.TryValidateActivation(
                _profile, new string('a', 32), out var normalized, out var error);

            Assert.That(accepted, Is.True);
            Assert.That(error, Is.Null);
            Assert.That(normalized, Is.EqualTo(Path.GetFullPath(_profile).TrimEnd(Path.DirectorySeparatorChar)));
        }

        [Test]
        public void ActivationRejectsMissingMarker()
        {
            File.Delete(Path.Combine(_profile, RealTimeTestProfileBootstrap.MarkerFile));

            var accepted = TestControlBridgePolicy.TryValidateActivation(
                _profile, new string('a', 32), out _, out var error);

            Assert.That(accepted, Is.False);
            Assert.That(error, Is.EqualTo("isolated-profile-marker-missing"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("short")]
        public void ActivationRejectsMissingOrShortToken(string token)
        {
            var accepted = TestControlBridgePolicy.TryValidateActivation(
                _profile, token, out _, out var error);

            Assert.That(accepted, Is.False);
            Assert.That(error, Is.EqualTo("session-token-too-short"));
        }

        [Test]
        public void ResolveSavePathAcceptsOnlyZksFileInsideIsolatedSaveFolder()
        {
            var path = TestControlBridgePolicy.ResolveSavePath(_profile, "Combat-test.zks");

            Assert.That(path, Is.EqualTo(Path.Combine(_profile, "Saved Games", "Combat-test.zks")));
        }

        [TestCase("..\\outside.zks")]
        [TestCase("folder\\save.zks")]
        [TestCase("save.json")]
        [TestCase("")]
        public void ResolveSavePathRejectsTraversalSubdirectoriesAndWrongExtensions(string fileName)
        {
            Assert.Throws<InvalidDataException>(() =>
                TestControlBridgePolicy.ResolveSavePath(_profile, fileName));
        }
    }
}
