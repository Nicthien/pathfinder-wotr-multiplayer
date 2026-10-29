using System.Linq;
using System.Reflection;
using NUnit.Framework;
using WOTRMultiplayer.HarmonyPatches.Combat;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeAreaAuthorityTests
    {
        [Test]
        public void AreaSchedulerTargetsExistInInstalledAssembly()
        {
            var targets = RealTimeAreaAuthorityPatches.Targets().ToArray();
            Assert.That(targets.Length, Is.EqualTo(6));
            Assert.That(targets.Distinct().Count(), Is.EqualTo(6));
            Assert.That(targets.Cast<MethodInfo>().All(x => x.ReturnType == typeof(void)), Is.True);
        }

        [Test]
        public void AreaComponentCallbacksExistInInstalledAssembly()
        {
            var targets = RealTimeAreaCallbackPatches.Targets().ToArray();
            Assert.That(targets.Length, Is.EqualTo(6));
            Assert.That(targets.Distinct().Count(), Is.EqualTo(6));
            Assert.That(targets.Cast<MethodInfo>().All(x => x.ReturnType == typeof(void)), Is.True);
        }

        [Test]
        public void AreaGuardsArePrototypeOnly()
        {
            Assert.That(RealTimeAreaAuthorityPatches.Prepare(), Is.EqualTo(RealTimePresentationRuntime.Enabled));
            Assert.That(RealTimeAreaCallbackPatches.Prepare(), Is.EqualTo(RealTimePresentationRuntime.Enabled));
        }

        [Test]
        public void InactiveClientDoesNotBlockNativeAreaSimulation()
        {
            Assert.That(RealTimePresentationRuntime.IsClientActive, Is.False);
            Assert.That(RealTimeAreaAuthorityPatches.BeforeSimulation(), Is.True);
            foreach (var target in RealTimeAreaCallbackPatches.Targets())
                Assert.That(RealTimeAreaCallbackPatches.BeforeCallback(target, null), Is.True);
        }
    }
}
