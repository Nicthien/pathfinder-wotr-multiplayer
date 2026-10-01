using System;
using System.IO;
using NUnit.Framework;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeBuildFingerprintTests
    {
        [Test]
        public void IdenticalPayloadMatchesAndChangedFrenchTextDoesNot()
        {
            var first = CreateFixture();
            var second = CreateFixture();
            try
            {
                Assert.That(RealTimeBuildFingerprint.Compute(first),
                    Is.EqualTo(RealTimeBuildFingerprint.Compute(second)));

                File.WriteAllText(Path.Combine(second, "localization", "frFR.json"), "changed");
                Assert.That(RealTimeBuildFingerprint.Compute(first),
                    Is.Not.EqualTo(RealTimeBuildFingerprint.Compute(second)));
            }
            finally
            {
                DeleteFixture(first);
                DeleteFixture(second);
            }
        }

        [Test]
        public void MissingRequiredPayloadFailsClosed()
        {
            var folder = CreateFixture();
            try
            {
                File.Delete(Path.Combine(folder, "WOTRMultiplayer.Networking.dll"));
                Assert.Throws<FileNotFoundException>(() => RealTimeBuildFingerprint.Compute(folder));
            }
            finally
            {
                DeleteFixture(folder);
            }
        }

        private static string CreateFixture()
        {
            var folder = Path.Combine(Path.GetTempPath(), "wotr-rtwp-build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "localization"));
            foreach (var name in new[] { "WOTRMultiplayer.dll", "WOTRMultiplayer.Networking.dll",
                         "WOTRMultiplayer.Localization.dll" })
                File.WriteAllText(Path.Combine(folder, name), name);
            File.WriteAllText(Path.Combine(folder, "localization", "enGB.json"), "english");
            File.WriteAllText(Path.Combine(folder, "localization", "frFR.json"), "francais");
            return folder;
        }

        private static void DeleteFixture(string folder)
        {
            foreach (var name in new[] { "WOTRMultiplayer.dll", "WOTRMultiplayer.Networking.dll",
                         "WOTRMultiplayer.Localization.dll" })
                File.Delete(Path.Combine(folder, name));
            File.Delete(Path.Combine(folder, "localization", "enGB.json"));
            File.Delete(Path.Combine(folder, "localization", "frFR.json"));
            Directory.Delete(Path.Combine(folder, "localization"));
            Directory.Delete(folder);
        }
    }
}
