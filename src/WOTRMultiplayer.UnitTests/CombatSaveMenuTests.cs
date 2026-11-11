using NUnit.Framework;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.UnitTests
{
    public class CombatSaveMenuTests
    {
        [TestCase(true, false, true, false)]
        [TestCase(true, false, false, true)]
        [TestCase(true, true, false, false)]
        [TestCase(false, false, false, false)]
        public void CombatRestrictionDoesNotBlockTheActiveHost(bool combat, bool checking, bool host, bool blocked)
            => Assert.That(CombatSaveNativePermission.ShouldBlockCombat(combat, checking, host), Is.EqualTo(blocked));
    }
}
