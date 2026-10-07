using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using Kingmaker.PubSubSystem;
using Kingmaker.UnitLogic.Mechanics;
using NUnit.Framework;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    [NonParallelizable]
    public class RealTimeNativeAreaRetirementTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static void Set(AreaEffectEntityData area, string field, object value) =>
            typeof(EntityDataBase).GetField(field, Fields).SetValue(area, value);
        private static AreaEffectEntityData Native(bool subscribed = false)
        {
            var area = new AreaEffectEntityData(default(JsonConstructorMark));
            Set(area, "Facts", new EntityFactsManager(area));
            Set(area, "Parts", new EntityPartsManager(area));
            Set(area, "<IsSubscribedOnEventBus>k__BackingField", subscribed);
            return area;
        }

        private static (UnitEntityData Unit, object[] Adapters) DamageAdapters(AreaEffectEntityData area)
        {
            var context = RealTimeAreaReplicaFactoryTests.Source().Context;
            var unit = (UnitEntityData)FormatterServices.GetUninitializedObject(typeof(UnitEntityData));
            typeof(UnitEntityData).GetField("<Descriptor>k__BackingField", Fields).SetValue(unit,
                FormatterServices.GetUninitializedObject(typeof(Kingmaker.UnitLogic.UnitDescriptor)));
            typeof(EntityDataBase).GetField("<UniqueId>k__BackingField", Fields).SetValue(unit, "retirement-test-caster");
            var proxy = new EntityService.Proxy(unit.UniqueId) { Entity = unit };
            typeof(EntityDataBase).GetField("<Proxy>k__BackingField", Fields).SetValue(unit, proxy);
            typeof(MechanicsContext).GetField("m_CasterRef", Fields).SetValue(context, new EntityRef<UnitEntityData>(unit));
            Assert.That(context.MaybeCaster, Is.SameAs(unit));
            typeof(AreaEffectEntityData).GetField("m_Context", Fields).SetValue(area, context);
            var type = typeof(AreaEffectEntityData).GetNestedType("DamageTriggerAdapter", BindingFlags.NonPublic);
            var adapters = Enumerable.Range(0, 2).Select(_ => Activator.CreateInstance(type,
                Fields, null, new object[] { null, area }, null)).ToArray();
            var list = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(type));
            foreach (var adapter in adapters) list.Add(adapter);
            Assert.That(((IInitiatorRulebookSubscriber)adapters[0]).GetSubscribingUnit(), Is.SameAs(unit));
            typeof(AreaEffectEntityData).GetField("m_DamageTriggersCopies", Fields).SetValue(area, list);
            return (unit, adapters);
        }

        private static IList[] RuleLists(UnitEntityData unit) => ((IDictionary)AccessTools.Field(
            RulebookEventBus.InitiatorRulebookSubscribers.Get(unit).GetType(), "m_Listeners")
                .GetValue(RulebookEventBus.InitiatorRulebookSubscribers.Get(unit)))
            .Values.Cast<object>().Select(list => (IList)AccessTools.Field(list.GetType(), "List").GetValue(list)).ToArray();

        [Test]
        public void NativeDamageAdaptersRestoreExactSharedCallbackOrder()
        {
            var area = Native(); var world = new object(); var source = DamageAdapters(area);
            foreach (var adapter in source.Adapters) EventBus.Subscribe(adapter);
            try
            {
                var lists = RuleLists(source.Unit);
                var original = lists.Select(list => list.Cast<object>().ToArray()).ToArray();
                Assert.That(lists.Length, Is.EqualTo(3));
                using (new RealTimeNativeAreaRetirement(area, world))
                    Assert.That(lists.All(list => list.Count == 0), Is.True);
                for (var i = 0; i < lists.Length; i++) Assert.That(lists[i], Is.EqualTo(original[i]));
                Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.False);
            }
            finally { foreach (var adapter in source.Adapters) EventBus.Unsubscribe(adapter); }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void DuplicateOrPartialNativeAdapterMembershipIsRefused(bool duplicate)
        {
            var area = Native(); var world = new object(); var source = DamageAdapters(area);
            foreach (var adapter in source.Adapters) EventBus.Subscribe(adapter);
            var list = RuleLists(source.Unit)[0]; var original = list.Cast<object>().ToArray();
            try
            {
                if (duplicate) list.Add(source.Adapters[0]); else list.Remove(source.Adapters[0]);
                Assert.Throws<InvalidOperationException>(() => new RealTimeNativeAreaRetirement(area, world));
                Assert.That(RealTimeAreaReplicaFactory.IsReplica(area), Is.False);
                Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.False);
            }
            finally
            {
                list.Clear(); foreach (var value in original) list.Add(value);
                foreach (var adapter in source.Adapters) EventBus.Unsubscribe(adapter);
            }
        }

        [Test]
        public void SubscriptionAppearingOnInitiallyUnsubscribedAreaPreventsCommit()
        {
            var area = Native(); var membership = false; var world = new object();
            var retirement = new RealTimeNativeAreaRetirement(area, world,
                _ => membership = false, _ => membership = true, _ => membership);
            membership = true;
            Assert.Throws<InvalidOperationException>(() => retirement.Commit());
            // An unexpected subscriber is not silently erased by rollback.
            Assert.Throws<InvalidOperationException>(() => retirement.Dispose());
            Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.True);
            membership = false; retirement.Dispose();
            Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.False);
        }

        [Test]
        public void DispatchInProgressRefusesBeforeMutatingNativeSubscriptions()
        {
            var area = Native(); var world = new object(); var source = DamageAdapters(area);
            foreach (var adapter in source.Adapters) EventBus.Subscribe(adapter);
            var manager = RulebookEventBus.InitiatorRulebookSubscribers.Get(source.Unit);
            var listener = ((IDictionary)AccessTools.Field(manager.GetType(), "m_Listeners").GetValue(manager))
                .Values.Cast<object>().First();
            var executing = (Kingmaker.Utility.CountableFlag)AccessTools.Field(listener.GetType(), "Executing").GetValue(listener);
            executing.Retain();
            try
            {
                Assert.Throws<InvalidOperationException>(() => new RealTimeNativeAreaRetirement(area, world));
                Assert.That(RealTimeAreaReplicaFactory.IsReplica(area), Is.False);
                Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.False);
            }
            finally { executing.Release(); foreach (var adapter in source.Adapters) EventBus.Unsubscribe(adapter); }
        }

        [Test]
        public void DamageAdapterOwnershipCannotChangeDuringSuspension()
        {
            var area = Native(); var world = new object(); var source = DamageAdapters(area);
            var retirement = new RealTimeNativeAreaRetirement(area, world);
            var field = typeof(AreaEffectEntityData).GetField("m_DamageTriggersCopies", Fields);
            var list = (IList)field.GetValue(area);
            list.RemoveAt(0);
            Assert.Throws<InvalidOperationException>(() => retirement.Commit());
            list.Insert(0, source.Adapters[0]); retirement.Dispose();
            Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.False);
        }

        [Test]
        public void RetiredDamageCallbacksRemainInertWithoutMultiplayerConnection()
        {
            var area = Native(); var source = DamageAdapters(area); var world = new object();
            Assert.That(WOTRMultiplayer.HarmonyPatches.Combat.RealTimeRetiredAreaDamagePatches.Targets().Count(), Is.EqualTo(3));
            using (new RealTimeNativeAreaRetirement(area, world))
                foreach (var adapter in source.Adapters)
                    Assert.That(WOTRMultiplayer.HarmonyPatches.Combat.RealTimeRetiredAreaDamagePatches.BeforeDamage(adapter), Is.False);
            foreach (var adapter in source.Adapters)
                Assert.That(WOTRMultiplayer.HarmonyPatches.Combat.RealTimeRetiredAreaDamagePatches.BeforeDamage(adapter), Is.True);
        }

        [Test]
        public void RegistryAndPoolRollbackPrecedeReleasingNativeSafetyMarker()
        {
            var area = Native(); var world = new object();
            Set(area, "<UniqueId>k__BackingField", "native-retirement");
            var service = new EntityService(); var scene = new SceneEntitiesState("test");
            var pool = new System.Collections.Generic.List<AreaEffectEntityData> { area };
            var proxy = new EntityService.Proxy(area.UniqueId) { Entity = area };
            // Native-world fixture populated as raw bookkeeping, never Register.
            typeof(EntityDataBase).GetField("<Proxy>k__BackingField", Fields).SetValue(area, proxy);
            typeof(EntityDataBase).GetField("<HoldingState>k__BackingField", Fields).SetValue(area, scene);
            var entities = (IDictionary)typeof(EntityService).GetField("m_Entities", Fields).GetValue(service);
            entities.Add(area.UniqueId, proxy);
            scene.AllEntityData.Add(area);
            ((EntityListsCache)typeof(EntityService).GetField("m_Cache", Fields).GetValue(service)).Add(area);
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                transaction.SuspendNativeArea(area, world);
                transaction.RemoveArea(service, scene, area);
                transaction.TrackAreaPool(pool, area, true);
                Assert.That(pool, Is.Empty); Assert.That(proxy.Entity, Is.Null);
            }
            Assert.That(pool, Is.EqualTo(new[] { area }));
            Assert.That(proxy.Entity, Is.SameAs(area)); Assert.That(area.Proxy, Is.SameAs(proxy));
            Assert.That(scene.AllEntityData, Does.Contain(area));
            Assert.That(RealTimeAreaReplicaFactory.IsReplica(area), Is.False);
            Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.False);
        }

        [Test]
        public void FailedPoolRollbackKeepsNativeAreaSuspended()
        {
            var area = Native(true); var world = new object();
            Set(area, "<UniqueId>k__BackingField", "conflicting-native-area");
            // Use actual event membership, not an invented subscribed flag.
            EventBus.Subscribe(area);
            var pool = new System.Collections.Generic.List<AreaEffectEntityData> { area };
            var transaction = new RealTimeBuffRestoreTransaction();
            try
            {
                transaction.SuspendNativeArea(area, world);
                transaction.TrackAreaPool(pool, area, true);
                var conflict = Native(); Set(conflict, "<UniqueId>k__BackingField", area.UniqueId);
                pool.Add(conflict);
                Assert.Throws<AggregateException>(() => transaction.Dispose());
                Assert.That(EventBus.IsGloballySubscribed(area), Is.False);
                Assert.That(RealTimeAreaReplicaFactory.IsReplica(area), Is.True);
                Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.True);
            }
            finally { EventBus.Unsubscribe(area); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SuspensionRollsBackSubscriptionsAndExternalObligation(bool subscribed)
        {
            var area = Native(subscribed); var world = new object(); var membership = subscribed;
            var unsubscribes = 0; var subscribes = 0;
            using (var retirement = new RealTimeNativeAreaRetirement(area, world,
                _ => { membership = false; unsubscribes++; },
                _ => { membership = true; subscribes++; }, _ => membership))
            {
                retirement.ValidateForCommit();
                Assert.That(membership, Is.False);
                Assert.That(area.IsSubscribedOnEventBus, Is.False);
                Assert.That(RealTimeAreaReplicaFactory.IsReplica(area), Is.True);
                Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.True);
                Assert.Throws<InvalidOperationException>(() => RealTimeNativeGraphRepair.RequireMechanicalState(world));
            }
            Assert.That(membership, Is.EqualTo(subscribed));
            Assert.That(area.IsSubscribedOnEventBus, Is.EqualTo(subscribed));
            Assert.That(unsubscribes, Is.EqualTo(subscribed ? 1 : 0));
            Assert.That(subscribes, Is.EqualTo(subscribed ? 1 : 0));
            Assert.That(RealTimeAreaReplicaFactory.IsReplica(area), Is.False);
            Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.False);
        }

        [Test]
        public void CommittedRemovalRetainsObligationOutsideAllPools()
        {
            var area = Native(); var world = new object();
            using (var retirement = new RealTimeNativeAreaRetirement(area, world, _ => {}, _ => {}, _ => false))
                retirement.Commit();
            Assert.That(RealTimeAreaReplicaFactory.IsReplica(area), Is.True);
            Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.True);
            Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(new object()), Is.False);
        }

        [Test]
        public void UnsubscribeMutatingThenThrowingIsRolledBack()
        {
            var area = Native(true); var world = new object(); var membership = true;
            Assert.Throws<InvalidOperationException>(() => new RealTimeNativeAreaRetirement(area, world,
                _ => { membership = false; throw new InvalidOperationException("injected"); },
                _ => membership = true, _ => membership));
            Assert.That(membership, Is.True);
            Assert.That(area.IsSubscribedOnEventBus, Is.True);
            Assert.That(RealTimeAreaReplicaFactory.IsReplica(area), Is.False);
            Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.False);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void MidwayUnsubscribeFailurePreservesUntouchedSubscriptions(int failureIndex)
        {
            var area = Native(true); var world = new object(); var source = DamageAdapters(area);
            var initial = new[] { (object)area }.Concat(source.Adapters).ToArray();
            var membership = new System.Collections.Generic.HashSet<object>(initial);
            var index = 0;
            Assert.Throws<InvalidOperationException>(() => new RealTimeNativeAreaRetirement(area, world,
                subscriber =>
                {
                    membership.Remove(subscriber);
                    if (index++ == failureIndex) throw new InvalidOperationException("injected-midway");
                }, subscriber => membership.Add(subscriber), membership.Contains));
            Assert.That(membership.SetEquals(initial), Is.True);
            Assert.That(area.IsSubscribedOnEventBus, Is.True);
            Assert.That(RealTimeAreaReplicaFactory.IsReplica(area), Is.False);
            Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.False);
        }

        [Test]
        public void FailedRollbackCannotReleaseSafetyObligation()
        {
            var area = Native(true); var world = new object(); var membership = true;
            var retirement = new RealTimeNativeAreaRetirement(area, world,
                _ => membership = false, _ => throw new InvalidOperationException("injected"), _ => membership);
            Assert.Throws<AggregateException>(() => retirement.Dispose());
            Assert.That(RealTimeAreaReplicaFactory.IsReplica(area), Is.True);
            Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.True);
        }

        [Test]
        public void FlagMismatchRefusesBeforeChangingAnything()
        {
            var area = Native(true); var world = new object();
            Assert.Throws<InvalidOperationException>(() => new RealTimeNativeAreaRetirement(area, world,
                _ => Assert.Fail("must not unsubscribe"), _ => {}, _ => false));
            Assert.That(RealTimeAreaReplicaFactory.IsReplica(area), Is.False);
            Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.False);
        }

        [Test]
        public void ReappearingSubscriptionPreventsCommit()
        {
            var area = Native(true); var world = new object(); var membership = true;
            using (var retirement = new RealTimeNativeAreaRetirement(area, world,
                _ => membership = false, _ => membership = true, _ => membership))
            {
                membership = true;
                Assert.Throws<InvalidOperationException>(() => retirement.Commit());
            }
            Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.False);
        }

        [Test]
        public void ActualNativeGlobalSubscriptionIsRemovedAndRestoredWithoutWorld()
        {
            var area = Native(); var world = new object();
            EventBus.Subscribe(area);
            Set(area, "<IsSubscribedOnEventBus>k__BackingField", true);
            try
            {
                Assert.That(EventBus.IsGloballySubscribed(area), Is.True);
                using (new RealTimeNativeAreaRetirement(area, world))
                    Assert.That(EventBus.IsGloballySubscribed(area), Is.False);
                Assert.That(EventBus.IsGloballySubscribed(area), Is.True);
                Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.False);
            }
            finally { EventBus.Unsubscribe(area); Set(area, "<IsSubscribedOnEventBus>k__BackingField", false); }
        }

        [Test]
        public void ViewlessNativeRetirementCanRollbackInTransaction()
        {
            var area = Native(); var world = new object();
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                transaction.SuspendNativeArea(area, world);
                Assert.That(transaction.HasChanges, Is.True);
            }
            Assert.That(RealTimeAreaReplicaFactory.IsReplica(area), Is.False);
            Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.False);
        }

        [Test]
        public void ViewlessNativeRetirementCanCommitButNotUnlockMechanicalReuse()
        {
            var area = Native(); var world = new object();
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                transaction.SuspendNativeArea(area, world);
                var checkpoint = new WOTRMultiplayer.Entities.Combat.NetworkCombatState();
                transaction.VerifyAndCommit(checkpoint, checkpoint);
            }
            Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.True);
        }

        [Test]
        public void MissingMechanicalManagersCannotBeSilentlyRetired()
        {
            Assert.That(RealTimeNativeAreaRetirement.CanRetire(new AreaEffectEntityData(default(JsonConstructorMark))), Is.False);
            Assert.That(RealTimeNativeAreaRetirement.CanRetire(null), Is.False);
        }
    }
}
