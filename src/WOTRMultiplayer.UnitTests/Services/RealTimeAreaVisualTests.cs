using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.View.MapObjects;
using NUnit.Framework;
using WOTRMultiplayer.HarmonyPatches.Combat;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeAreaVisualTests
    {
        [TestCase(AreaEffectShape.Cylinder)]
        [TestCase(AreaEffectShape.Wall)]
        [TestCase(AreaEffectShape.AllArea)]
        public void OrdinaryShapesAreSupported(AreaEffectShape shape) =>
            Assert.That(RealTimeAreaVisual.SupportsShape(shape), Is.True);

        [Test]
        public void UnknownShapeIsNotSilentlyRenderedAsEmpty() =>
            Assert.That(RealTimeAreaVisual.SupportsShape((AreaEffectShape)int.MaxValue), Is.False);

        [Test]
        public void UnreviewedScriptsAreRejected()
        {
            Assert.That(RealTimeAreaVisual.IsAllowedScript(typeof(AreaEffectView)), Is.False);
            Assert.That(RealTimeAreaVisual.IsAllowedScript(null), Is.False);
        }

        [Test]
        public void NativeLifecycleIsPreservedForOrdinaryViews()
        {
            var view = (AreaEffectView)FormatterServices.GetUninitializedObject(typeof(AreaEffectView));
            foreach (var method in RealTimeAreaVisualLifecyclePatches.Targets())
                Assert.That(RealTimeAreaVisualLifecyclePatches.BeforeNativeLifecycle(view, method), Is.True, method.Name);
        }

        [Test]
        public void OwnedLifecycleRemainsBlockedWithoutConnectionOrGame()
        {
            var view = (AreaEffectView)FormatterServices.GetUninitializedObject(typeof(AreaEffectView));
            RealTimeAreaVisual.MarkOwned(view);
            var methods = RealTimeAreaVisualLifecyclePatches.Targets().ToList();
            Assert.That(methods.Select(method => method.Name), Is.EquivalentTo(new[]
                { "OnEnable", "OnDisable", "OnDestroy", "CreateEntityData", "SpawnFxs", "UpdateViewActive" }));
            foreach (var method in methods)
                Assert.That(RealTimeAreaVisualLifecyclePatches.BeforeNativeLifecycle(view, method), Is.False, method.Name);
        }
    }
}
