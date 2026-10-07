using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NUnit.Framework;
using Owlcat.Runtime.Core.Logging;
using WOTRMultiplayer.HarmonyPatches.GameInstance;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class RealTimeNativeClientLoadTests
    {
        private static IEnumerable<MethodBase> LifecycleTargets() => RealTimeNativeLifecycleFailurePatches.TargetMethods();

        [TestCaseSource(nameof(LifecycleTargets))]
        public void NativeLifecycleCatchSitesRetainOriginalLogsAndAddFaultObservation(MethodBase method)
        {
            var input = PatchProcessor.GetOriginalInstructions(method);
            var logger = AccessTools.Method(typeof(LogChannel), nameof(LogChannel.Exception),
                new[] { typeof(Exception), typeof(string), typeof(object[]) });
            var report = AccessTools.Method(typeof(RealTimeNativeLifecycleFailurePatches), nameof(RealTimeNativeLifecycleFailurePatches.Observe));
            var count = input.Count(instruction => instruction.Calls(logger));
            Assert.That(count, Is.GreaterThan(0));
            var generator = new DynamicMethod("lifecycle-il-only", typeof(void), Type.EmptyTypes).GetILGenerator();
            var rewritten = RealTimeNativeLifecycleFailurePatches.Transpile(input, generator, method).ToList();
            Assert.That(rewritten.Count(instruction => instruction.Calls(logger)), Is.EqualTo(count));
            Assert.That(rewritten.Count(instruction => instruction.Calls(report)), Is.EqualTo(count));
        }

        [Test]
        public void LifecycleFaultOutsideOwnedLoadDoesNotCreateOwnership()
        {
            RealTimeNativeLifecycleFailurePatches.Observe(new InvalidOperationException(), "synthetic");
            Assert.That(RealTimeNativeLoadLease.IsActive, Is.False);
        }

        [Test]
        public void LifecycleCallbackFaultIsStickyAndPreventsLoadedReceipt()
        {
            var info = Info();
            using var lease = RealTimeNativeLoadLease.Begin(new object(), info, () => true, (_, _) => { });
            RealTimeNativeLoadLease.AdmitAreaLoad(info);
            RealTimeNativeLifecycleFailurePatches.Observe(null, "ignored");
            Assert.That(lease.Fault, Is.Null);
            RealTimeNativeLifecycleFailurePatches.Observe(new InvalidOperationException(), "first-callback");
            RealTimeNativeLifecycleFailurePatches.Observe(new Exception(), "second-callback");
            lease.MarkNativeCallback();
            Assert.That(lease.Fault, Is.EqualTo("native-lifecycle-callback-failed:first-callback:System.InvalidOperationException"));
            Assert.That(lease.NativeCallbackCompleted, Is.False);
            Assert.That(RealTimeNativeLoadLease.EnterHydration(info), Is.Null);
        }

        private static int _syntheticLogs;
        private static Exception _syntheticLogged;
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyntheticLog(object logger, Exception error, string message, object[] arguments)
        { _syntheticLogs++; _syntheticLogged = error; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyntheticLifecycle()
        {
            try { throw new InvalidOperationException("caught-by-native-wrapper"); }
            catch (Exception error) { SyntheticLog(null, error, "native-message", Array.Empty<object>()); }
        }

        private static IEnumerable<CodeInstruction> SyntheticTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            => RealTimeNativeLifecycleFailurePatches.Rewrite(instructions, generator, "synthetic-wrapper",
                AccessTools.Method(typeof(RealTimeNativeClientLoadTests), nameof(SyntheticLog)));

        [Test]
        public void CaughtSyntheticCallbackRetainsLoggingButCannotCertifySuccessfulLoad()
        {
            var harmony = new Harmony("rtwp.unit.native-lifecycle-catch");
            var method = AccessTools.Method(typeof(RealTimeNativeClientLoadTests), nameof(SyntheticLifecycle));
            _syntheticLogs = 0; _syntheticLogged = null;
            try
            {
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(RealTimeNativeClientLoadTests), nameof(SyntheticTranspiler)));
                SyntheticLifecycle();
                Assert.That(_syntheticLogs, Is.EqualTo(1));
                Assert.That(RealTimeNativeLoadLease.IsActive, Is.False);
                var info = Info();
                using var lease = RealTimeNativeLoadLease.Begin(new object(), info, () => true, (_, _) => { });
                RealTimeNativeLoadLease.AdmitAreaLoad(info);
                Assert.DoesNotThrow(SyntheticLifecycle);
                Assert.That(_syntheticLogs, Is.EqualTo(2));
                Assert.That(_syntheticLogged.Message, Is.EqualTo("caught-by-native-wrapper"));
                Assert.That(lease.Fault, Is.EqualTo("native-lifecycle-callback-failed:synthetic-wrapper:System.InvalidOperationException"));
                lease.MarkNativeCallback();
                Assert.That(lease.NativeCallbackCompleted, Is.False);
            }
            finally { harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id); }
        }
    }
}
