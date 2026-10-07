using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Stats;
using Owlcat.Runtime.Core.Logging;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.HarmonyPatches.GameInstance
{
    // The native wrappers catch callback exceptions and continue, sometimes
    // setting IsPostLoadExecuted even on failure. Observe those exceptions at
    // the catch's logging site, without changing the original logging/control flow.
    [HarmonyPatch]
    public static class RealTimeNativeLifecycleFailurePatches
    {
        [HarmonyPrepare]
        public static bool Prepare() => RealTimePresentationRuntime.Enabled;

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in new[] { typeof(EntityDataBase), typeof(EntityPart), typeof(EntityFact) })
                foreach (var name in new[] { "PostLoad", type == typeof(EntityFact) ? "Activate" : "TurnOn" })
                    yield return AccessTools.DeclaredMethod(type, name)
                        ?? throw new MissingMethodException(type.FullName, name);
            foreach (var name in new[] { "PostLoad", "Activate" })
                yield return AccessTools.DeclaredMethod(typeof(EntityFactComponent), name)
                    ?? throw new MissingMethodException(typeof(EntityFactComponent).FullName, name);
            foreach (var type in new[] { typeof(CharacterStats), typeof(ModifiableValue) })
                yield return AccessTools.DeclaredMethod(type, "PostLoad")
                    ?? throw new MissingMethodException(type.FullName, "PostLoad");
        }

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions,
            ILGenerator generator, MethodBase original)
            => Rewrite(instructions, generator, original.DeclaringType.FullName + "." + original.Name,
                AccessTools.Method(typeof(LogChannel), nameof(LogChannel.Exception),
                    new[] { typeof(Exception), typeof(string), typeof(object[]) }));

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions,
            ILGenerator generator, string origin, MethodInfo logException)
        {
            if (logException == null) throw new MissingMethodException("native-lifecycle-exception-log-missing");
            var arguments = generator.DeclareLocal(typeof(object[]));
            var message = generator.DeclareLocal(typeof(string));
            var report = AccessTools.Method(typeof(RealTimeNativeLifecycleFailurePatches), nameof(Observe));
            var sites = 0;
            foreach (var instruction in instructions)
            {
                var current = new CodeInstruction(instruction);
                if (current.Calls(logException))
                {
                    sites++;
                    var first = new CodeInstruction(OpCodes.Stloc, arguments);
                    first.labels.AddRange(current.labels); current.labels.Clear();
                    first.blocks.AddRange(current.blocks); current.blocks.Clear();
                    yield return first;
                    yield return new CodeInstruction(OpCodes.Stloc, message);
                    // Stack still holds the logger and exception. Duplicate only
                    // the exception; the original native log call remains intact.
                    yield return new CodeInstruction(OpCodes.Dup);
                    yield return new CodeInstruction(OpCodes.Ldstr, origin);
                    yield return new CodeInstruction(OpCodes.Call, report);
                    yield return new CodeInstruction(OpCodes.Ldloc, message);
                    yield return new CodeInstruction(OpCodes.Ldloc, arguments);
                }
                yield return current;
            }
            if (sites == 0) throw new InvalidOperationException("native-lifecycle-exception-sites-missing:" + origin);
        }

        public static void Observe(Exception error, string origin)
        {
            if (error == null) return;
            // Only an owned native load is affected. Ordinary/TB play retains
            // the native exception handling; first fault stays sticky on the lease.
            RealTimeNativeLoadLease.RecordFault("native-lifecycle-callback-failed:" + origin + ":" + error.GetType().FullName);
        }
    }
}
