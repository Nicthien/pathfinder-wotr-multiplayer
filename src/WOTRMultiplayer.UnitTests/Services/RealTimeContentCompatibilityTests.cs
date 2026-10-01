using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Content;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeContentCompatibilityTests
    {
        [Test]
        public void MatchingGameProtocolAndBuildAreCompatible()
        {
            var host = CompatibleState();
            var client = CompatibleState();

            Assert.That(RealTimeContentCompatibility.Compare(host, client),
                Is.EqualTo(RealTimeContentMismatch.None));
        }

        [Test]
        public void EveryMismatchIsReportedAtConnection()
        {
            var host = CompatibleState();
            var client = CompatibleState();
            client.GameVersion = "other-game";
            client.RealTimeProtocolVersion--;
            client.RealTimeBuildFingerprint = "other-build";

            Assert.That(RealTimeContentCompatibility.Compare(host, client),
                Is.EqualTo(RealTimeContentMismatch.GameVersion
                    | RealTimeContentMismatch.ProtocolVersion
                    | RealTimeContentMismatch.BuildFingerprint));
        }

        [Test]
        public void MissingOrMalformedContentFailsClosed()
        {
            var host = CompatibleState();

            Assert.That(RealTimeContentCompatibility.Compare(host, null),
                Is.Not.EqualTo(RealTimeContentMismatch.None));
            Assert.That(RealTimeContentCompatibility.Compare(null, host),
                Is.Not.EqualTo(RealTimeContentMismatch.None));
            Assert.That(RealTimeContentCompatibility.Compare(host, new NetworkContentState()),
                Is.Not.EqualTo(RealTimeContentMismatch.None));
        }

        private static NetworkContentState CompatibleState() => new()
        {
            GameVersion = "2.7.0",
            RealTimeProtocolVersion = NetworkRealTimeCombatState.ProtocolVersion,
            RealTimeBuildFingerprint = "test-build"
        };
    }
}
