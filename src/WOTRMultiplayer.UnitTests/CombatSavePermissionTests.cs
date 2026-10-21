using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Kingmaker;
using NUnit.Framework;
using WOTRMultiplayer.HarmonyPatches.Combat;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.UnitTests
{
    public class CombatSavePermissionTests
    {
        [Test]
        public void ReplacesOnlyNativeCombatTest()
        {
            var getter = AccessTools.PropertyGetter(typeof(Player), nameof(Player.IsInCombat));
            var untouched = new CodeInstruction(OpCodes.Ldc_I4_0);
            var result = SaveManagerRealTimePatches.SaveManager_IsSaveAllowed_Transpiler(new[] {
                new CodeInstruction(OpCodes.Callvirt, getter), untouched }).ToArray();
            Assert.That(result[0].Calls(AccessTools.Method(typeof(CombatSaveNativePermission), "IsBlockingCombat")), Is.True);
            Assert.That(result[1], Is.SameAs(untouched));
        }

        [TestCase(0)]
        [TestCase(2)]
        public void UnsupportedLayoutDoesNotBypassAnything(int calls)
        {
            var getter = AccessTools.PropertyGetter(typeof(Player), nameof(Player.IsInCombat));
            var source = Enumerable.Range(0, calls).Select(_ => new CodeInstruction(OpCodes.Callvirt, getter)).ToArray();
            var result = SaveManagerRealTimePatches.SaveManager_IsSaveAllowed_Transpiler(source).ToArray();
            Assert.That(result.Count(i => i.Calls(getter)), Is.EqualTo(calls));
        }

        [Test]
        public void NoHostCannotAcquireNativePermission()
        {
            Assert.That(CombatSaveNativePermission.Check(null), Is.False);
            Assert.That(CombatSaveNativePermission.IsChecking, Is.False);
        }
    }
}
