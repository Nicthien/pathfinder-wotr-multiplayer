using System;
using System.Linq;
using System.Runtime.Serialization;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using Kingmaker.UnitLogic.Abilities;
using NUnit.Framework;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeAreaReplicaStoreTests
    {
        private static AreaEffectEntityData Replica(string id = "zone")
        {
            var source = RealTimeAreaReplicaFactoryTests.Source(); source.State.Id = id;
            return RealTimeAreaReplicaFactory.CreateDetached(source.Blueprint, source.Context, source.State, _ => null,
                _ => (AbilityParams)FormatterServices.GetUninitializedObject(typeof(AbilityParams)));
        }
        private static void Register(EntityService service, SceneEntitiesState scene, AreaEffectEntityData area)
        {
            using var registration = RealTimeAreaReplicaStore.Register(service, scene, area, "room");
            registration.Commit();
        }
        private static NetworkCombatState Checkpoint(string id = "zone", float orientation = 90) => new()
        { AreaEffects = [new NetworkAreaEffect { Id = id, Orientation = orientation }] };

        [Test]
        public void RegistrationUpdatesNativeCachesWithoutSceneEvents()
        {
            var service = new EntityService(); var scene = new SceneEntitiesState("room"); var area = Replica();
            var cachedAreas = service.GetListUnsafe<AreaEffectEntityData>();
            var cachedAll = service.GetListUnsafe(); var events = 0;
            Action<SceneEntitiesState, EntityDataBase> callback = (_, _) => events++;
            SceneEntitiesState.OnAdded += callback;
            try
            {
                Register(service, scene, area);
                Assert.That(service.GetEntity("zone"), Is.SameAs(area));
                Assert.That(cachedAreas.Single(), Is.SameAs(area));
                Assert.That(cachedAll.Single(), Is.SameAs(area));
                Assert.That(scene.AllEntityData.Single(), Is.SameAs(area));
                Assert.That(area.HoldingState, Is.SameAs(scene));
                Assert.That(area.Proxy.Entity, Is.SameAs(area));
                Assert.That(events, Is.Zero);
                Assert.That(area.IsTurnedOn, Is.False);
            }
            finally { SceneEntitiesState.OnAdded -= callback; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AbortedRegistrationRestoresCreatedOrExistingProxy(bool placeholderExists)
        {
            var service = new EntityService(); var scene = new SceneEntitiesState("room"); var area = Replica();
            var placeholder = placeholderExists ? service.GetProxy("zone") : null;
            var cache = service.GetListUnsafe<AreaEffectEntityData>();
            EntityService.Proxy stagedProxy;
            using (RealTimeAreaReplicaStore.Register(service, scene, area, "room"))
            {
                stagedProxy = area.Proxy;
                Assert.That(service.GetEntity("zone"), Is.SameAs(area));
            }
            Assert.That(service.GetEntity("zone"), Is.Null);
            Assert.That(cache, Is.Empty); Assert.That(scene.AllEntityData, Is.Empty);
            Assert.That(area.HoldingState, Is.Null); Assert.That(area.Proxy, Is.Null);
            Assert.That(stagedProxy.IsDisposed, Is.EqualTo(!placeholderExists));
            if (placeholderExists)
            {
                Assert.That(service.GetProxy("zone"), Is.SameAs(placeholder));
                Assert.That(placeholder.Entity, Is.Null); Assert.That(placeholder.IsDisposed, Is.False);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemovalRestoresOrDisposesSameProxyWithoutSceneEvents(bool commit)
        {
            var service = new EntityService(); var scene = new SceneEntitiesState("room");
            var first = Replica("first"); var area = Replica(); var last = Replica("last");
            Register(service, scene, first); Register(service, scene, area); Register(service, scene, last);
            var proxy = area.Proxy; var cache = service.GetListUnsafe<AreaEffectEntityData>(); var events = 0;
            Action<SceneEntitiesState, EntityDataBase> callback = (_, _) => events++;
            SceneEntitiesState.OnRemoved += callback;
            try
            {
                using (var removal = RealTimeAreaReplicaStore.RemoveReplica(service, scene, area))
                {
                    Assert.That(service.GetEntity("zone"), Is.Null);
                    Assert.That(cache, Does.Not.Contain(area));
                    Assert.That(area.HoldingState, Is.Null);
                    if (commit) removal.Commit();
                }
                Assert.That(events, Is.Zero);
                if (commit)
                {
                    Assert.That(proxy.IsDisposed, Is.True); Assert.That(area.Proxy, Is.Null);
                    Assert.That(service.GetEntity("zone"), Is.Null);
                }
                else
                {
                    Assert.That(area.Proxy, Is.SameAs(proxy)); Assert.That(proxy.Entity, Is.SameAs(area));
                    Assert.That(scene.AllEntityData[1], Is.SameAs(area));
                    Assert.That(cache.Count(x => ReferenceEquals(x, area)), Is.EqualTo(1));
                }
            }
            finally { SceneEntitiesState.OnRemoved -= callback; }
        }

        [TestCase("ordinary")]
        [TestCase("scene")]
        [TestCase("duplicate")]
        [TestCase("case-collision")]
        public void InvalidRegistrationDoesNotDamageExistingWorld(string cause)
        {
            var service = new EntityService(); var scene = new SceneEntitiesState("room"); var original = Replica();
            Register(service, scene, original);
            var next = cause == "ordinary" ? new AreaEffectEntityData(default(JsonConstructorMark))
                : Replica(cause == "case-collision" ? "ZONE" : cause == "scene" ? "other" : "zone");
            Assert.Throws<InvalidOperationException>(() => RealTimeAreaReplicaStore.Register(service, scene, next,
                cause == "scene" ? "wrong-room" : "room"));
            Assert.That(scene.AllEntityData.Single(), Is.SameAs(original));
            Assert.That(service.GetEntity("zone"), Is.SameAs(original));
            Assert.That(next.HoldingState, Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CheckpointVerificationCommitsOrRollsBackAreaRegistration(bool matches)
        {
            var service = new EntityService(); var scene = new SceneEntitiesState("room"); var area = Replica();
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                transaction.RegisterArea(service, scene, area, "room");
                Assert.That(transaction.HasChanges, Is.True);
                if (matches) transaction.VerifyAndCommit(Checkpoint(), Checkpoint());
                else Assert.Throws<InvalidOperationException>(() => transaction.VerifyAndCommit(Checkpoint(), Checkpoint(orientation: 91)));
            }
            Assert.That(service.GetEntity("zone"), matches ? Is.SameAs(area) : Is.Null);
            Assert.That(scene.AllEntityData.Count, Is.EqualTo(matches ? 1 : 0));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SameIdReplacementPreservesProxyIdentityOnCommitAndRollback(bool commit)
        {
            var service = new EntityService(); var scene = new SceneEntitiesState("room");
            var old = Replica(); Register(service, scene, old); var proxy = old.Proxy;
            var next = Replica(); var cache = service.GetListUnsafe<AreaEffectEntityData>();
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                transaction.RemoveArea(service, scene, old);
                transaction.RegisterArea(service, scene, next, "room");
                if (commit) transaction.VerifyAndCommit(Checkpoint(), Checkpoint());
            }
            var retained = commit ? next : old; var removed = commit ? old : next;
            Assert.That(service.GetEntity("zone"), Is.SameAs(retained));
            Assert.That(retained.Proxy, Is.SameAs(proxy)); Assert.That(proxy.Entity, Is.SameAs(retained));
            Assert.That(proxy.IsDisposed, Is.False);
            Assert.That(cache.Single(), Is.SameAs(retained));
            Assert.That(scene.AllEntityData.Single(), Is.SameAs(retained));
            Assert.That(removed.Proxy, Is.Null); Assert.That(removed.HoldingState, Is.Null);
        }

        [Test]
        public void TemporalMismatchRollsBackAreaRemoval()
        {
            var service = new EntityService(); var scene = new SceneEntitiesState("room"); var area = Replica();
            Register(service, scene, area); var proxy = area.Proxy;
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                transaction.RemoveArea(service, scene, area);
                var expected = Checkpoint(); var actual = Checkpoint();
                actual.AreaEffects[0].DurationTicks = TimeSpan.FromSeconds(10).Ticks;
                Assert.Throws<InvalidOperationException>(() => transaction.VerifyAndCommit(expected, actual));
            }
            Assert.That(service.GetEntity("zone"), Is.SameAs(area));
            Assert.That(area.Proxy, Is.SameAs(proxy));
        }

        [Test]
        public void TransactionCannotAcceptAreasAfterCommitOrDispose()
        {
            var service = new EntityService(); var scene = new SceneEntitiesState("room");
            var transaction = new RealTimeBuffRestoreTransaction();
            transaction.VerifyAndCommit(Checkpoint(), Checkpoint());
            Assert.Throws<InvalidOperationException>(() => transaction.RegisterArea(service, scene, Replica(), "room"));
            transaction.Dispose();
            Assert.Throws<ObjectDisposedException>(() => transaction.RegisterArea(service, scene, Replica(), "room"));
        }

        [Test]
        public void DuplicateReplacementOperationsAreRefusedBeforeWorldChanges()
        {
            var service = new EntityService(); var scene = new SceneEntitiesState("room"); var original = Replica();
            Register(service, scene, original); var proxy = original.Proxy; var next = Replica();
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                transaction.RemoveArea(service, scene, original);
                transaction.RegisterArea(service, scene, next, "room");
                Assert.Throws<InvalidOperationException>(() => transaction.RemoveArea(service, scene, next));
                Assert.Throws<InvalidOperationException>(() => transaction.RegisterArea(service, scene, Replica(), "room"));
                Assert.That(service.GetEntity("zone"), Is.SameAs(next));
            }
            Assert.That(service.GetEntity("zone"), Is.SameAs(original));
            Assert.That(original.Proxy, Is.SameAs(proxy));
        }

        [Test]
        public void OrdinaryAreaCannotBeRemovedByReplicaStore()
        {
            Assert.Throws<InvalidOperationException>(() => RealTimeAreaReplicaStore.RemoveReplica(
                new EntityService(), new SceneEntitiesState("room"), new AreaEffectEntityData(default(JsonConstructorMark))));
        }
    }
}
