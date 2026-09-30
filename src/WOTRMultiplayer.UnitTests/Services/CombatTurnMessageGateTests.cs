using NUnit.Framework;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class CombatTurnMessageGateTests
    {
        [Test]
        public void MatchingCurrentTurnAndSender_IsAccepted()
        {
            Assert.That(CombatTurnMessageGate.IsCurrentEndMessage("unit-a", "UNIT-A", 42, 42, true), Is.True);
        }

        [Test]
        public void StaleTurnOrSpoofedSender_IsRejected()
        {
            Assert.That(CombatTurnMessageGate.IsCurrentEndMessage("unit-b", "unit-a", 42, 42, true), Is.False);
            Assert.That(CombatTurnMessageGate.IsCurrentEndMessage("unit-a", "unit-a", 99, 42, true), Is.False);
            Assert.That(CombatTurnMessageGate.IsCurrentEndMessage(null, "unit-a", 42, 42, true), Is.False);
        }
    }
}
