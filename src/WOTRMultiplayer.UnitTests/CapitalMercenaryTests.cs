using System;
using System.Linq;
using HarmonyLib;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using NUnit.Framework;
using WOTRMultiplayer.HarmonyPatches;

namespace WOTRMultiplayer.UnitTests
{
    public class CapitalMercenaryTests
    {
        [TestCase(true, true, false, true, true, false, true)]
        [TestCase(false, true, false, true, true, false, false)]
        [TestCase(true, false, false, true, true, false, false)]
        [TestCase(true, true, true, true, true, false, false)]
        [TestCase(true, true, false, false, true, false, false)]
        [TestCase(true, true, false, true, false, false, false)]
        [TestCase(true, true, false, true, true, true, false)]
        public void EligibilityPreservesSoloStoryReserveAndScriptedUnits(bool multi, bool capital,
            bool tactical, bool custom, bool active, bool scripted, bool expected)
            => Assert.That(CapitalMercenaryPatches.IsEligible(multi, capital, tactical, custom, active, scripted), Is.EqualTo(expected));

        [TestCase(false, 1, "IsMainOrCapitalMercenary")]
        [TestCase(true, 2, "IsMainOrControllableMercenary")]
        public void InstalledEngineHasExpectedPatchSites(bool control, int count, string helper)
        {
            var method = control
                ? AccessTools.PropertyGetter(typeof(UnitEntityData), nameof(UnitEntityData.IsDirectlyControllable))
                : AccessTools.Method(typeof(Player), "AddCharacterToLists");
            var original = PatchProcessor.GetOriginalInstructions(method).ToList();
            var patched = CapitalMercenaryPatches.ReplaceMainCheck(original, helper, count).ToList();
            Assert.That(patched.Count, Is.EqualTo(original.Count));
            Assert.That(patched.Count(x => x.Calls(AccessTools.Method(typeof(CapitalMercenaryPatches), helper))), Is.EqualTo(count));
        }

        [Test]
        public void UnexpectedEngineLayoutIsRejected()
            => Assert.Throws<InvalidOperationException>(() => CapitalMercenaryPatches.ReplaceMainCheck(
                Array.Empty<CodeInstruction>(), "IsMainOrCapitalMercenary", 1).ToList());
    }
}
