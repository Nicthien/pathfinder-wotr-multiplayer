using NUnit.Framework;
using WOTRMultiplayer.HarmonyPatches.Combat;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeCombatJoinAuthorityTests
    {
        [Test]
        public void PresentationClientRejectsNativeJoinWithoutConsultingTurnBasedGate()
        {
            var consulted = false;
            Assert.That(UnitEntityDataPatches.ShouldAllowJoin(true, true, () =>
            {
                consulted = true;
                return true;
            }), Is.False);
            Assert.That(consulted, Is.False);
        }

        [TestCase(false, false, true)]
        [TestCase(true, false, false)]
        [TestCase(true, true, true)]
        public void OtherModesKeepExistingJoinDecision(bool multiplayer, bool gate, bool expected)
        {
            var consulted = false;
            var allowed = UnitEntityDataPatches.ShouldAllowJoin(multiplayer, false, () =>
            {
                consulted = true;
                return gate;
            });
            Assert.That(allowed, Is.EqualTo(expected));
            Assert.That(consulted, Is.EqualTo(multiplayer));
        }

        [Test]
        public void PresentationClientRejectsCombatPreparationTickWithoutConsultingTurnBasedGate()
        {
            var consulted = false;
            Assert.That(UnitCombatPrepareControllerPatches.ShouldTick(true, true, () =>
            {
                consulted = true;
                return true;
            }), Is.False);
            Assert.That(consulted, Is.False);
        }

        [TestCase("<default-unit>", true, false)]
        [TestCase("UN_real_1", false, false)]
        [TestCase("UN_real_1", true, true)]
        public void UnitResultsArePublishedOnlyForRegisteredWorldEntities(string id, bool registered, bool expected)
            => Assert.That(RealTimeUnitResultCapture.CanPublishUnit(id, registered), Is.EqualTo(expected));
    }
}
