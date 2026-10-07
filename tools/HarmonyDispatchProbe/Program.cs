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
            success &= ProbeAreas();
            success &= ProbeBuffReplica();
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= trace; }
        return success ? 0 : 1;
    }

    private static int _statCallbacks;

    private sealed class ContextMapperStub : RealProxy
    {
        public ContextMapperStub() : base(typeof(AutoMapper.IRuntimeMapper)) { }
        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message;
            var method = (MethodInfo)call.MethodBase;
            if (call.MethodName == "Map" && method.ReturnType == typeof(NetworkAbilityParams))
                return new ReturnMessage(new NetworkAbilityParams { CasterLevel = 12, SpellLevel = 6 }, null, 0,
                    call.LogicalCallContext, call);
            return new ReturnMessage(new InvalidOperationException("Unexpected context mapping: " + call.MethodName), call);
        }
    }

    private static bool ProbeBuffReplica()
    {
        try
        {
            var blueprint = (Kingmaker.UnitLogic.Buffs.Blueprints.BlueprintBuff)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Kingmaker.UnitLogic.Buffs.Blueprints.BlueprintBuff));
            var context = (Kingmaker.UnitLogic.Mechanics.MechanicsContext)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Kingmaker.UnitLogic.Mechanics.MechanicsContext));
            blueprint.AssetGuid = Kingmaker.Blueprints.BlueprintGuid.Parse("11111111111111111111111111111111");
            var owner = (Kingmaker.EntitySystem.Entities.UnitEntityData)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Kingmaker.EntitySystem.Entities.UnitEntityData));
            var manager = new Kingmaker.EntitySystem.EntityFactsManager(owner);
            var initiallyEmpty = !manager.GetAll<Kingmaker.UnitLogic.Buffs.Buff>().Any();
            var buff = RealTimeBuffReplicaStore.CreateDetached(blueprint, context, new NetworkBuff
            {
                Id = "offline-host-buff", BlueprintId = blueprint.AssetGuid.ToString(), Rank = 3,
                TimeLeft = TimeSpan.FromSeconds(12), NextTickTime = TimeSpan.MaxValue, NextResourceSpendingTime = TimeSpan.MaxValue
            }, TimeSpan.FromSeconds(100));
            bool registered;
            using (RealTimeBuffReplicaStore.Register(manager, buff))
                registered = ReferenceEquals(manager.GetAll<Kingmaker.UnitLogic.Buffs.Buff>().Single(), buff)
                    && ReferenceEquals(manager.FindById(buff.UniqueId), buff) && !buff.IsActive && !buff.IsSubscribedOnEventBus;
            var rolledBack = !manager.GetAll<Kingmaker.UnitLogic.Buffs.Buff>().Any() && buff.Manager == null;
            var replicaHarmony = new Harmony("WOTRMultiplayer.OfflineProbe.replica-lifecycle");
            var replicaGuards = replicaHarmony.CreateClassProcessor(typeof(RealTimeBuffReplicaLifecyclePatches)).Patch();
            if (replicaGuards == null || replicaGuards.Count != 5) throw new InvalidOperationException("Replica lifecycle guards were not installed.");
            try
            {
                using (var rollback = new RealTimeBuffRestoreTransaction())
                {
                    rollback.Register(manager, buff, null);
                    foreach (var target in RealTimeBuffReplicaLifecyclePatches.Targets())
                        ((MethodInfo)target).Invoke(buff, target.GetParameters().Length == 0 ? null : new object[] { TimeSpan.Zero });
                    if (!ReferenceEquals(manager.FindById(buff.UniqueId), buff)) throw new InvalidOperationException("Replica removed itself.");
                    var expected = new WOTRMultiplayer.Entities.Combat.NetworkCombatState();
                    var actual = new WOTRMultiplayer.Entities.Combat.NetworkCombatState
                        { Units = new System.Collections.Generic.List<NetworkUnit> { new NetworkUnit { Id = "unexpected" } } };
                    try { rollback.VerifyAndCommit(expected, actual); throw new InvalidOperationException("Bad checkpoint was committed."); }
                    catch (InvalidOperationException mismatch) when (mismatch.Message == "buff-restoration-checkpoint-hash-mismatch") { }
                }
                rolledBack &= !manager.GetAll<Kingmaker.UnitLogic.Buffs.Buff>().Any() && buff.Manager == null;
                Console.WriteLine("installed-buff-restoration: guarded-callbacks=" + replicaGuards.Count + "; mismatch-rollback=" + rolledBack);
                using (var registration = RealTimeBuffReplicaStore.Register(manager, buff)) registration.Commit();
                using (var removal = new RealTimeBuffRestoreTransaction()) removal.Remove(manager, buff, null);
                var removalRolledBack = ReferenceEquals(manager.GetAll<Kingmaker.UnitLogic.Buffs.Buff>().Single(), buff);
                using (var removal = new RealTimeBuffRestoreTransaction())
                {
                    removal.Remove(manager, buff, null);
                    removal.VerifyAndCommit(new WOTRMultiplayer.Entities.Combat.NetworkCombatState(),
                        new WOTRMultiplayer.Entities.Combat.NetworkCombatState());
                }
                var removalCommitted = !manager.GetAll<Kingmaker.UnitLogic.Buffs.Buff>().Any() && buff.Manager == null;
                rolledBack &= removalRolledBack && removalCommitted;
                Console.WriteLine("installed-buff-removal: rollback=" + removalRolledBack + "; commit=" + removalCommitted);
            }
            finally
            {
                foreach (var target in RealTimeBuffReplicaLifecyclePatches.Targets())
                    replicaHarmony.Unpatch(target, HarmonyPatchType.All, replicaHarmony.Id);
            }
            var initialized = (Kingmaker.UnitLogic.Mechanics.MechanicsContext)Activator.CreateInstance(
                typeof(Kingmaker.UnitLogic.Mechanics.MechanicsContext), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { default(Kingmaker.EntitySystem.Persistence.JsonUtility.JsonConstructorMark) }, null);
            typeof(Kingmaker.UnitLogic.Mechanics.MechanicsContext).GetField("AssociatedBlueprint").SetValue(initialized, blueprint);
            var ranks = (int[])typeof(Kingmaker.UnitLogic.Mechanics.MechanicsContext).GetField("m_Ranks",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(initialized);
            ranks[0] = 37;
            var captured = RealTimeBuffContextCapture.Capture(initialized,
                (AutoMapper.IRuntimeMapper)new ContextMapperStub().GetTransparentProxy());
            ranks[0] = 99;
            var contextCopied = captured.Count == 1 && captured[0].Ranks[0] == 37 && captured[0].Params.SpellLevel == 6;
            captured[0].Ranks[0] = 15;
            var confirmedParameters = (Kingmaker.UnitLogic.Abilities.AbilityParams)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(Kingmaker.UnitLogic.Abilities.AbilityParams));
            typeof(Kingmaker.UnitLogic.Abilities.AbilityParams).GetProperty("DC").GetSetMethod(true)
                .Invoke(confirmedParameters, new object[] { 23 });
            RealTimeBuffContextReplication.Apply(initialized, captured, _ => confirmedParameters);
            var contextApplied = ranks[0] == 15 && initialized.Params.DC == 23;
            captured[0].MainTarget = new WOTRMultiplayer.Entities.NetworkTargetWrapper(
                new WOTRMultiplayer.Entities.NetworkVector3(4, 5, 6), 0.5f, null);
            var restored = RealTimeBuffContextFactory.Create(captured, _ => blueprint,
                _ => throw new InvalidOperationException("unexpected-unit-lookup"),
                _ => throw new InvalidOperationException("unexpected-item-lookup"),
                _ => throw new InvalidOperationException("unexpected-ability-lookup"), _ => confirmedParameters);
            var contextRestored = RealTimeBuffContextReplication.Validate(restored, captured) == null
                && restored.Params.DC == 23 && restored.ParentContext == null;
            Console.WriteLine("installed-buff-context-factory: restored-without-world=" + contextRestored);
            Console.WriteLine("installed-buff-context: captured-without-world=" + contextCopied + "; applied-without-recalculation=" + contextApplied);
            Console.WriteLine("installed-buff-replica: empty=" + initiallyEmpty + "; registered-without-components=" + registered
                + "; cache-rollback=" + rolledBack);
            return initiallyEmpty && registered && rolledBack && contextCopied && contextApplied && contextRestored;
        }
        catch (Exception failure) { Console.WriteLine("installed-buff-replica: FAILED: " + failure); return false; }
    }
    private static void StatChanged(ModifiableValue stat, int previous) { _statCallbacks++; }

    private static bool ProbeAreas()
    {
        var harmony = new Harmony("WOTRMultiplayer.OfflineProbe.installed-areas");
        var multiplayer = typeof(WOTRMultiplayer.Main).GetField("<Multiplayer>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        var previous = multiplayer.GetValue(null);
        try
        {
            multiplayer.SetValue(null, new MultiplayerStub().GetTransparentProxy());
            _suppress = true; _reports = 0;
            var scheduler = harmony.CreateClassProcessor(typeof(RealTimeAreaAuthorityPatches)).Patch();
            var callbacks = harmony.CreateClassProcessor(typeof(RealTimeAreaCallbackPatches)).Patch();
            if (scheduler?.Count != 6 || callbacks?.Count != 6)
                throw new InvalidOperationException("Area guards were not installed.");
            // All originals would touch the world or component graph. Invoke
            // patched native methods on inert objects: no Game.Instance/view.
            var area = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                typeof(Kingmaker.EntitySystem.Entities.AreaEffectEntityData));
            foreach (var target in RealTimeAreaAuthorityPatches.Targets().Cast<MethodInfo>())
            {
                var arguments = target.GetParameters().Select(p => p.ParameterType.IsValueType
                    ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
                target.Invoke(area, arguments);
            }
            var blueprint = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                typeof(Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbilityAreaEffect));
            foreach (var target in RealTimeAreaCallbackPatches.Targets().Cast<MethodInfo>())
            {
                var arguments = new object[target.GetParameters().Length];
                arguments[1] = area;
                target.Invoke(blueprint, arguments);
            }
            var blocked = _reports == 6;
            var replica = new Kingmaker.EntitySystem.Entities.AreaEffectEntityData(
                default(Kingmaker.EntitySystem.Persistence.JsonUtility.JsonConstructorMark));
            var state = new WOTRMultiplayer.Entities.AreaEffects.NetworkAreaEffect
            {
                Position = new WOTRMultiplayer.Entities.NetworkVector3(2, 3, 4), Orientation = 120,
                CreationGameTimeTicks = 100, DurationTicks = 200
            };
            RealTimeAreaReplication.Apply(replica, state, _ => null, _ => null);
            var areaType = replica.GetType();
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var rawApplied = (float)areaType.GetField("m_Rotation", fields).GetValue(replica) == 120
                && ((TimeSpan)areaType.GetField("m_CreationTime", fields).GetValue(replica)).Ticks == 100
                && ((TimeSpan?)areaType.GetField("m_Duration", fields).GetValue(replica)).Value.Ticks == 200
                && _reports == 6;
            state.DurationTicks = null;
            RealTimeAreaReplication.Apply(replica, state, _ => null, _ => null);
            rawApplied &= areaType.GetField("m_Duration", fields).GetValue(replica) == null;
            _suppress = false;
            // Do not execute host originals on synthetic worldless objects.
            var hostAllowed = RealTimeAreaAuthorityPatches.BeforeSimulation()
                && RealTimeAreaCallbackPatches.Targets().All(target =>
                    RealTimeAreaCallbackPatches.BeforeCallback(target, null));
            Console.WriteLine("installed-area-authority: scheduler-targets=" + scheduler.Count
                + "; callback-targets=" + callbacks.Count + "; blocked-without-world=" + blocked
                + "; host-prefix-allows=" + hostAllowed + "; raw-state-without-world=" + rawApplied);
            return blocked && hostAllowed && rawApplied;
        }
        catch (Exception failure) { Console.WriteLine("installed-area-authority: FAILED: " + failure); return false; }
        finally
        {
            multiplayer.SetValue(null, previous);
            foreach (var method in Harmony.GetAllPatchedMethods().ToArray())
                if (Harmony.GetPatchInfo(method)?.Owners.Contains(harmony.Id) == true)
                    harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
        }
    }

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
            var buffGuards = harmony.CreateClassProcessor(typeof(RealTimeBuffAuthorityPatches)).Patch();
            if (buffGuards == null || buffGuards.Count != 3) throw new InvalidOperationException("Buff collection guards were not installed.");
            var collection = (Kingmaker.UnitLogic.Buffs.BuffCollection)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                typeof(Kingmaker.UnitLogic.Buffs.BuffCollection));
            foreach (var target in RealTimeBuffAuthorityPatches.Targets())
                ((MethodInfo)target).Invoke(collection, target.GetParameters().Length == 0 ? null : new object[] { null });
            Console.WriteLine("installed-buff-scheduling: guarded-targets=" + buffGuards.Count + "; no-world-access=True");
            _suppress = false;
            damageSetter.Invoke(descriptor, new object[] { 3 });
            var hostPreserved = descriptor.Damage == 3 && _reports == 2;
            var additional = new ModifiableValue(null, StatType.AdditionalAttackBonus);
            changed.SetValue(additional, Delegate.CreateDelegate(changed.FieldType,
                typeof(Program).GetMethod(nameof(StatChanged), BindingFlags.Static | BindingFlags.NonPublic)));
            _suppress = true;
            var reportsBeforeStats = _reports;
            typeof(ModifiableValue).GetProperty("BaseValue").GetSetMethod().Invoke(additional, new object[] { 99 });
            var nonVitalBlocked = additional.BaseValue == 0 && _reports == reportsBeforeStats + 1;
            RealTimeCalculatedStatReplication.Apply(new[] { additional }, new[]
            {
                new NetworkCalculatedStat { Type = (int)StatType.AdditionalAttackBonus,
                    Values = new NetworkStatValues { BaseValue = 10, ModifiedValue = -3, ModifiedValueRaw = -3, PermanentValue = 8 } }
            });
            var calculated = RealTimeCalculatedStatReplication.Capture(new[] { additional });
            var calculatedApplied = calculated[0].Values.ModifiedValue == -3 && additional.BaseValue == 10 && _statCallbacks == 0;
            _suppress = false;
            Console.WriteLine("installed-calculated-stats: client-setter-blocked=" + nonVitalBlocked
                + "; applied-without-recalculation=" + calculatedApplied);
            var player = (Kingmaker.Player)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Kingmaker.Player));
            typeof(Kingmaker.Player).GetProperty("IsInCombat").GetSetMethod(true).Invoke(player, new object[] { true });
            RealTimeUnitResultReplication.ApplyPartyCombatEnd(player);
            var partyEnded = !player.IsInCombat;
            var buff = (Kingmaker.UnitLogic.Buffs.Buff)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                typeof(Kingmaker.UnitLogic.Buffs.Buff));
            RealTimeBuffReplication.ApplyValues(buff, new NetworkBuff
            {
                Rank = 4, TimeLeft = TimeSpan.FromSeconds(12), NextTickTime = TimeSpan.FromSeconds(-1),
                NextResourceSpendingTime = TimeSpan.MaxValue
            }, TimeSpan.FromSeconds(100));
            var buffApplied = buff.Rank == 4 && buff.EndTime == TimeSpan.FromSeconds(112)
                && buff.NextResourceSpendingTime == TimeSpan.MaxValue;
            Console.WriteLine("installed-buff-values: applied-without-world-owner-blueprint=" + buffApplied);
            Console.WriteLine("installed-vitals: patched-targets=" + patched.Count + "; blocked-client-setters=" + blocked
                + "; applied-confirmed-death=" + applied + "; stat-callbacks=" + _statCallbacks + "; host-setter=" + hostPreserved
                + "; party-combat-cleared=" + partyEnded);
            return blocked && applied && hostPreserved && partyEnded && buffApplied && nonVitalBlocked && calculatedApplied;
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
