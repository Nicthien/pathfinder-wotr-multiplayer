using System;
using System.Reflection;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using HarmonyLib;
using Kingmaker.RuleSystem;
using WOTRMultiplayer.HarmonyPatches.Combat;
using WOTRMultiplayer.Abstractions;
using Kingmaker.UnitLogic;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.Enums;
using WOTRMultiplayer.Services.GameInteraction;
using WOTRMultiplayer.Entities.Units;

// Offline diagnostic. No game process, Game.Instance, Unity initialization,
// saves, installation writes or real gameplay units/results are involved.
internal static class Program
{
    private static bool _suppress = true;
    private static int _reports;
    private static int _nativeCalls;
    private static Type _observedRuleType;
    private sealed class MultiplayerStub : RealProxy
    {
        public MultiplayerStub() : base(typeof(IMultiplayer)) { }
        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message;
            if (call.MethodName == "get_IsRealTimeClientCombatActive")
                return new ReturnMessage(_suppress, null, 0, call.LogicalCallContext, call);
            // This fixture must never enter host publishing or Game.Instance.
            if (call.MethodName == "get_IsRealTimeCombatActive")
                return new ReturnMessage(false, null, 0, call.LogicalCallContext, call);
            if (call.MethodName == "ReportRealTimeAIActionFault")
            {
                _reports++;
                return new ReturnMessage(null, null, 0, call.LogicalCallContext, call);
            }
            return new ReturnMessage(new InvalidOperationException("Unexpected production dependency: " + call.MethodName), call);
        }
    }
    private sealed class RuleAttackProbe : RulebookEvent
    {
        public RuleAttackProbe() : base(null)
        {
            Reason = (RuleReason)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(RuleReason));
        }
        public override bool DisableBattleLog => false;
        public override void OnTrigger(RulebookEventContext context) { _nativeCalls++; }
    }
    private class ProbeEvent { public bool IsTriggered => false; }
    private sealed class DerivedEvent : ProbeEvent { }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Dispatch<T>(T evt) where T : ProbeEvent => evt.IsTriggered ? 2 : typeof(T) == typeof(DerivedEvent) ? 3 : 1;

    private static bool BeforeGate(ref bool __result)
    {
        if (!_suppress) return true;
        __result = true; return false;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Ordinary() => 1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Generic<T>(T value) where T : class => typeof(T) == typeof(string) ? 3 : 1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Execute<T>(T value, Action<T> action) where T : class => action(value);
    private static bool BeforeExecute() => !_suppress;

    private static bool Before(ref int __result)
    {
        if (!_suppress) return true;
        __result = 2; return false;
    }

    private static int Main(string[] args)
    {
        if (args.Length != 0 && args.Length != 2) return 2;
        if (args.Length == 2)
        {
            var fingerprint = WOTRMultiplayer.Services.RealTimeBuildFingerprint.Compute(args[0]);
            Console.WriteLine("CandidateFingerprint=" + fingerprint);
            if (!string.Equals(fingerprint, args[1], StringComparison.Ordinal)) return 2;
        }
        Console.WriteLine("Runtime=" + Environment.Version + "; Mono=" + (Type.GetType("Mono.Runtime") != null)
            + "; Harmony=" + typeof(Harmony).Assembly.FullName);
        Console.WriteLine("PresentationPrototype=" + WOTRMultiplayer.Services.RealTimePresentationRuntime.Enabled);
        var firstFailure = false;
        EventHandler<FirstChanceExceptionEventArgs> trace = (sender, args) =>
        {
            if (!(args.Exception is NotImplementedException) || firstFailure) return;
            firstFailure = true;
            Console.WriteLine("FIRST_NOT_IMPLEMENTED: " + args.Exception);
        };
        AppDomain.CurrentDomain.FirstChanceException += trace;
        var success = true;
        try
        {
            success &= Probe(typeof(Program).GetMethod(nameof(Ordinary), BindingFlags.Static | BindingFlags.NonPublic),
                () => Ordinary(), "ordinary", 1);
            var unsafeGenericPassed = Probe(typeof(Program).GetMethod(nameof(Generic), BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(typeof(object)), () => Generic("derived-reference"), "shared-generic", 3);
            Console.WriteLine("Rejected full-generic approach: " + !unsafeGenericPassed);
            var unsafeGatePassed = ProbeGate();
            Console.WriteLine("Rejected inlineable-property approach: " + !unsafeGatePassed);
            success &= ProbeDelegate();
            success &= ProbeRulebook();
            success &= ProbeVitals();
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= trace; }
        return success ? 0 : 1;
    }

    private static int _statCallbacks;
    private static void StatChanged(ModifiableValue stat, int previous) { _statCallbacks++; }

    private static bool ProbeVitals()
    {
        var harmony = new Harmony("WOTRMultiplayer.OfflineProbe.installed-vitals");
        var multiplayer = typeof(WOTRMultiplayer.Main).GetField("<Multiplayer>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        var previous = multiplayer.GetValue(null);
        try
        {
            multiplayer.SetValue(null, new MultiplayerStub().GetTransparentProxy());
            _suppress = true; _reports = 0; _statCallbacks = 0;
            var patched = harmony.CreateClassProcessor(typeof(RealTimeUnitResultPatches)).Patch();
            if (patched == null || patched.Count == 0) throw new InvalidOperationException("Vitals guard was not installed.");
            var descriptor = (UnitDescriptor)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(UnitDescriptor));
            var statsField = typeof(UnitDescriptor).GetField("Stats");
            var stats = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(statsField.FieldType);
            foreach (var name in new[] { "HitPoints", "TemporaryHitPoints", "DamageNonLethal" })
            {
                var field = stats.GetType().GetField(name);
                field.SetValue(stats, System.Runtime.Serialization.FormatterServices.GetUninitializedObject(field.FieldType));
            }
            statsField.SetValue(descriptor, stats);
            typeof(UnitDescriptor).GetField("State").SetValue(descriptor,
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(UnitState)));
            var stat = descriptor.Stats.DamageNonLethal;
            typeof(ModifiableValue).GetField("Type").SetValue(stat, StatType.DamageNonLethal);
            var changed = typeof(ModifiableValue).GetField("OnChanged", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            changed.SetValue(stat, Delegate.CreateDelegate(changed.FieldType,
                typeof(Program).GetMethod(nameof(StatChanged), BindingFlags.Static | BindingFlags.NonPublic)));
            // Invoke actual installed setters, not the prefixes directly.
            var damageSetter = typeof(UnitDescriptor).GetProperty("Damage").GetSetMethod();
            damageSetter.Invoke(descriptor, new object[] { 99 });
            typeof(ModifiableValue).GetProperty("BaseValue").GetSetMethod().Invoke(stat, new object[] { 12 });
            var blocked = descriptor.Damage == 0 && stat.BaseValue == 0 && _reports == 2 && _statCallbacks == 0;
            var values = new NetworkUnitVitals
            {
                Damage = 40, LifeState = 2, IsFinallyDead = true,
                HitPoints = new NetworkStatValues { BaseValue = 30, ModifiedValue = 30, ModifiedValueRaw = 30, PermanentValue = 30 },
                TemporaryHitPoints = new NetworkStatValues(),
                NonLethal = new NetworkStatValues { BaseValue = 2, ModifiedValue = 2, ModifiedValueRaw = 2, PermanentValue = 2 }
            };
            RealTimeUnitResultReplication.ApplyValues(descriptor, values);
            var applied = descriptor.Damage == 40 && descriptor.State.IsDead && descriptor.State.IsFinallyDead
                && stat.BaseValue == 2 && stat.ModifiedValue == 2 && _reports == 2 && _statCallbacks == 0;
            _suppress = false;
            damageSetter.Invoke(descriptor, new object[] { 3 });
            var hostPreserved = descriptor.Damage == 3 && _reports == 2;
            var player = (Kingmaker.Player)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Kingmaker.Player));
            typeof(Kingmaker.Player).GetProperty("IsInCombat").GetSetMethod(true).Invoke(player, new object[] { true });
            RealTimeUnitResultReplication.ApplyPartyCombatEnd(player);
            var partyEnded = !player.IsInCombat;
            Console.WriteLine("installed-vitals: patched-targets=" + patched.Count + "; blocked-client-setters=" + blocked
                + "; applied-confirmed-death=" + applied + "; stat-callbacks=" + _statCallbacks + "; host-setter=" + hostPreserved
                + "; party-combat-cleared=" + partyEnded);
            return blocked && applied && hostPreserved && partyEnded;
        }
        catch (Exception failure) { Console.WriteLine("installed-vitals: FAILED: " + failure); return false; }
        finally
        {
            multiplayer.SetValue(null, previous);
            foreach (var method in Harmony.GetAllPatchedMethods().ToArray())
                if (Harmony.GetPatchInfo(method)?.Owners.Contains(harmony.Id) == true)
                    harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
        }
    }

    private static void InvokeHostCallback<T>(T rule) where T : RulebookEvent
    {
        var method = ((MethodInfo)RealTimeRuleDispatchPatches.Target()).GetGenericMethodDefinition().MakeGenericMethod(typeof(T));
        Action<T> callback = value => { _observedRuleType = typeof(T); value.OnTrigger(new RulebookEventContext()); };
        method.Invoke(null, new object[] { rule, callback });
    }

    private static bool ProbeRulebook()
    {
        var method = RealTimeRuleDispatchPatches.Target();
        Console.WriteLine("RulebookModule=" + method.Module.ModuleVersionId);
        using (var hash = System.Security.Cryptography.SHA256.Create())
            Console.WriteLine("SaveExecuteIL=" + BitConverter.ToString(hash.ComputeHash(method.GetMethodBody().GetILAsByteArray())).Replace("-", ""));
        var harmony = new Harmony("WOTRMultiplayer.OfflineProbe.installed-rulebook");
        var installed = false;
        var multiplayer = typeof(WOTRMultiplayer.Main).GetField("<Multiplayer>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        var previousMultiplayer = multiplayer.GetValue(null);
        try
        {
            multiplayer.SetValue(null, new MultiplayerStub().GetTransparentProxy());
            var patched = harmony.CreateClassProcessor(typeof(RealTimeRuleDispatchPatches)).Patch();
            installed = patched != null && patched.Count > 0;
            if (!installed) throw new InvalidOperationException("Production prototype patch was not installed.");
            _suppress = true; _reports = 0; _nativeCalls = 0;
            var rulebook = new Rulebook();
            var first = new RuleAttackProbe();
            var second = new RuleAttackProbe();
            var returned = rulebook.TriggerEvent(first);
            rulebook.TriggerEvent(second);
            var noCallbacks = _nativeCalls == 0 && _reports == 2 && !first.IsTriggered && !second.IsTriggered;
            var cleanContext = rulebook.Context.StackSize == 0 && ReferenceEquals(first, returned);
            Console.WriteLine("installed-rulebook: blocked-native-calls=" + _nativeCalls + "; reports=" + _reports
                + "; context-stack=" + rulebook.Context.StackSize);
            _suppress = false;
            InvokeHostCallback(new RuleAttackProbe());
            var typedHost = _nativeCalls == 1 && _observedRuleType == typeof(RuleAttackProbe);
            Console.WriteLine("installed-rulebook: host-native-calls=" + _nativeCalls + "; typed-delegate=" + typedHost);
            return noCallbacks && cleanContext && typedHost;
        }
        catch (Exception error) { Console.WriteLine("installed-rulebook: FAILED: " + error); return false; }
        finally
        {
            multiplayer.SetValue(null, previousMultiplayer);
            if (installed) harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
        }
    }

    private static bool ProbeDelegate()
    {
        var method = typeof(Program).GetMethod(nameof(Execute), BindingFlags.Static | BindingFlags.NonPublic)
            .MakeGenericMethod(typeof(object));
        var harmony = new Harmony("WOTRMultiplayer.OfflineProbe.delegate");
        var installed = false;
        try
        {
            harmony.Patch(method, new HarmonyMethod(typeof(Program).GetMethod(nameof(BeforeExecute),
                BindingFlags.Static | BindingFlags.NonPublic)));
            installed = true;
            var count = 0;
            string observed = null;
            _suppress = true;
            Execute("blocked", value => { count++; observed = value; });
            var blockedCount = count;
            _suppress = false;
            Execute("allowed", value => { count++; observed = value; });
            Console.WriteLine("delegate-dispatch: blocked-count=" + blockedCount + "; passthrough-count=" + count
                + "; observed=" + observed);
            return blockedCount == 0 && count == 1 && observed == "allowed";
        }
        catch (Exception error) { Console.WriteLine("delegate-dispatch: FAILED: " + error); return false; }
        finally { if (installed) harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id); }
    }

    private static bool ProbeGate()
    {
        var method = typeof(ProbeEvent).GetProperty(nameof(ProbeEvent.IsTriggered)).GetGetMethod();
        var harmony = new Harmony("WOTRMultiplayer.OfflineProbe.gate");
        var installed = false;
        try
        {
            // Pre-warm the generic caller, as it may already have been JITed.
            Console.WriteLine("non-generic-gate: prewarm=" + Dispatch(new DerivedEvent()));
            harmony.Patch(method, new HarmonyMethod(typeof(Program).GetMethod(nameof(BeforeGate),
                BindingFlags.Static | BindingFlags.NonPublic)));
            installed = true;
            _suppress = true;
            var blocked = Dispatch(new DerivedEvent());
            _suppress = false;
            var allowed = Dispatch(new DerivedEvent());
            Console.WriteLine("non-generic-gate: blocked=" + blocked + "; expected=2; passthrough=" + allowed + "; expected=3");
            return blocked == 2 && allowed == 3;
        }
        catch (Exception error) { Console.WriteLine("non-generic-gate: FAILED: " + error); return false; }
        finally { if (installed) harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id); }
    }

    private static bool Probe(MethodInfo method, Func<int> invoke, string label, int expectedOriginal)
    {
        var harmony = new Harmony("WOTRMultiplayer.OfflineProbe." + label);
        var installed = false;
        try
        {
            harmony.Patch(method, new HarmonyMethod(typeof(Program).GetMethod(nameof(Before),
                BindingFlags.Static | BindingFlags.NonPublic)));
            installed = true;
            _suppress = true;
            var result = invoke();
            Console.WriteLine(label + ": result=" + result + "; expected=2");
            _suppress = false;
            var original = invoke();
            Console.WriteLine(label + ": passthrough=" + original + "; expected=" + expectedOriginal);
            return result == 2 && original == expectedOriginal;
        }
        catch (Exception failure)
        {
            Console.WriteLine(label + ": FAILED: " + failure);
            return false;
        }
        finally
        {
            if (installed)
                try { harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id); }
                catch (Exception cleanup) { Console.WriteLine(label + ": cleanup failed: " + cleanup); }
        }
    }
}
