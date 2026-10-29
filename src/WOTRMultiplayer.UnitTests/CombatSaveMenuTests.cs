using System.Collections.Generic;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions;
using WOTRMultiplayer.HarmonyPatches.MenuPatches;

namespace WOTRMultiplayer.UnitTests
{
    public class CombatSaveMenuTests
    {
        [Test]
        public void MenuPatchResolvesTheInstalledConstructorWithItsCloseCallback()
        {
            var method = typeof(EscMenuVMPatches).GetMethod(nameof(EscMenuVMPatches.EscMenuVM_Constructed_Postfix));
            var patch = (HarmonyLib.HarmonyPatch)System.Attribute.GetCustomAttribute(method, typeof(HarmonyLib.HarmonyPatch));
            Assert.That(patch.info.methodType, Is.EqualTo(HarmonyLib.MethodType.Constructor));
            Assert.That(patch.info.argumentTypes, Is.EqualTo(new[] { typeof(System.Action) }));
            Assert.That(HarmonyLib.AccessTools.Constructor(patch.info.declaringType, patch.info.argumentTypes), Is.Not.Null);
        }

        [TestCase(false, false, false, false, true)]
        [TestCase(true, false, false, false, true)]
        [TestCase(true, true, false, false, false)]
        [TestCase(true, true, true, true, false)]
        [TestCase(true, true, true, false, false)]
        public void ClassicSavePreservesNativeFlowOutsideCombatAndClosesMenuBeforeCoordinating(
            bool active, bool combat, bool available, bool singleSave, bool native)
        {
            var multiplayer = A.Fake<IMultiplayer>();
            A.CallTo(() => multiplayer.IsActive).Returns(active);
            A.CallTo(() => multiplayer.IsInCombat).Returns(combat);
            A.CallTo(() => multiplayer.CanSaveCombatFromMenu).Returns(available);
            var order = new List<string>();
            A.CallTo(() => multiplayer.SaveCombatFromMenu()).Invokes(() => order.Add("coordinated-save"));
            Assert.That(EscMenuVMPatches.RouteSave(multiplayer, singleSave, () => order.Add("close-menu")), Is.EqualTo(native));
            Assert.That(order, Is.EqualTo(active && combat && available && !singleSave
                ? new[] { "close-menu", "coordinated-save" } : new string[0]));
        }
    }
}
