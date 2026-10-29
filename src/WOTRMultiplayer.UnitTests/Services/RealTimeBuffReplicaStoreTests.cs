using System;
using System.Linq;
using System.Runtime.Serialization;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Buffs;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using Kingmaker.UnitLogic.Mechanics;
using NUnit.Framework;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeBuffReplicaStoreTests
    {
        private static T Bare<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));

        private static Buff Create(string id = "host-buff")
        {
            var blueprint = Bare<BlueprintBuff>();
            blueprint.AssetGuid = Kingmaker.Blueprints.BlueprintGuid.Parse("11111111111111111111111111111111");
            return RealTimeBuffReplicaStore.CreateDetached(blueprint, Bare<MechanicsContext>(), new NetworkBuff
            {
                Id = id, BlueprintId = blueprint.AssetGuid.ToString(), Rank = 3, TimeLeft = TimeSpan.FromSeconds(12),
                NextTickTime = TimeSpan.MaxValue, NextResourceSpendingTime = TimeSpan.MaxValue
            }, TimeSpan.FromSeconds(100));
        }

        private static EntityFactsManager Manager() => new(Bare<UnitEntityData>());

        [Test]
        public void DetachedReplicaUsesNativeEmptyConstructorWithoutComponentsOrWorld()
        {
            var buff = Create();
            Assert.That(buff.UniqueId, Is.EqualTo("host-buff"));
            Assert.That(buff.Rank, Is.EqualTo(3));
            Assert.That(buff.EndTime, Is.EqualTo(TimeSpan.FromSeconds(112)));
            Assert.That(buff.Context, Is.Not.Null);
            Assert.That(buff.Manager, Is.Null);
            Assert.That(buff.IsActive, Is.False);
            Assert.That(buff.IsTurnedOn, Is.False);
            Assert.That(buff.IsSubscribedOnEventBus, Is.False);
            var components = typeof(EntityFact).GetField("Components", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(buff);
            Assert.That((System.Collections.ICollection)components, Is.Empty);
            Assert.That(RealTimeBuffReplicaStore.IsReplica(buff), Is.True);
            Assert.That(RealTimeBuffReplicaStore.IsReplica(Bare<Buff>()), Is.False);
        }

        [Test]
        public void RegistrationAndRollbackUpdateBothNativeListAndPrewarmedCache()
        {
            var manager = Manager();
            Assert.That(manager.GetAll<Buff>(), Is.Empty); // Warm the native typed cache.
            var buff = Create();
            var registration = RealTimeBuffReplicaStore.Register(manager, buff);
            Assert.That(manager.FindById(buff.UniqueId), Is.SameAs(buff));
            Assert.That(manager.GetAll<Buff>().Single(), Is.SameAs(buff));
            Assert.That(buff.Manager, Is.SameAs(manager));
            Assert.That(buff.IsActive, Is.False);
            registration.Dispose(); registration.Dispose();
            Assert.That(manager.GetAll<Buff>(), Is.Empty);
            Assert.That(manager.FindById(buff.UniqueId), Is.Null);
            Assert.That(buff.Manager, Is.Null);
            Assert.Throws<ObjectDisposedException>(() => registration.Commit());
        }

        [Test]
        public void CommitRetainsRegisteredRepresentationWithoutActivation()
        {
            var manager = Manager(); var buff = Create();
            using (var registration = RealTimeBuffReplicaStore.Register(manager, buff)) registration.Commit();
            Assert.That(manager.GetAll<Buff>().Single(), Is.SameAs(buff));
            Assert.That(buff.IsActive, Is.False);
            Assert.That(buff.IsSubscribedOnEventBus, Is.False);
        }

        [Test]
        public void DuplicateOrMechanicalFactsCannotBeRegisteredAsReplicas()
        {
            var manager = Manager(); var first = Create();
            using (RealTimeBuffReplicaStore.Register(manager, first))
            {
                Assert.Throws<InvalidOperationException>(() => RealTimeBuffReplicaStore.Register(manager, Create("HOST-BUFF")));
                Assert.Throws<InvalidOperationException>(() => RealTimeBuffReplicaStore.Register(manager, first));
                Assert.Throws<InvalidOperationException>(() => RealTimeBuffReplicaStore.Register(manager, Bare<Buff>()));
                Assert.That(manager.GetAll<Buff>().Count(), Is.EqualTo(1));
            }
            Assert.That(manager.GetAll<Buff>(), Is.Empty);
        }

        [Test]
        public void RejectedSourceDoesNotCreateOrRegisterAnything()
        {
            var blueprint = Bare<BlueprintBuff>();
            Assert.Throws<InvalidOperationException>(() => RealTimeBuffReplicaStore.CreateDetached(blueprint,
                Bare<MechanicsContext>(), new NetworkBuff { Id = "id", BlueprintId = "different" }, TimeSpan.Zero));
        }

        [Test]
        public void ConfirmedProjectionKeepsHostAbilityIdentityAndIsMutationIsolated()
        {
            var blueprint = Bare<BlueprintBuff>();
            blueprint.AssetGuid = Kingmaker.Blueprints.BlueprintGuid.Parse("11111111111111111111111111111111");
            var host = new NetworkBuff
            {
                Id = "host-buff", BlueprintId = blueprint.AssetGuid.ToString(), Rank = 1,
                TimeLeft = TimeSpan.FromSeconds(12), NextTickTime = TimeSpan.MaxValue,
                NextResourceSpendingTime = TimeSpan.MaxValue,
                ContextChain = [new NetworkBuffContextState
                {
                    BlueprintId = "context-blueprint", Params = new NetworkAbilityParams(),
                    SourceAbility = new NetworkAbility { Id = "host-ability", BlueprintId = "ability-blueprint" }
                }]
            };
            var buff = RealTimeBuffReplicaStore.CreateDetached(blueprint, Bare<MechanicsContext>(), host,
                TimeSpan.FromSeconds(100));
            host.ContextChain[0].SourceAbility.Id = "caller-mutated";
            var projected = RealTimeBuffReplicaStore.ProjectConfirmed(buff, new NetworkBuff
            {
                Id = "host-buff", BlueprintId = blueprint.AssetGuid.ToString(), TimeLeft = TimeSpan.FromSeconds(9),
                NextTickTime = TimeSpan.FromSeconds(3), NextResourceSpendingTime = TimeSpan.FromSeconds(4),
                ContextChain = [new NetworkBuffContextState
                {
                    Params = new NetworkAbilityParams(),
                    SourceAbility = new NetworkAbility { Id = "client-ability" }
                }]
            });

            Assert.That(projected.ContextChain[0].SourceAbility.Id, Is.EqualTo("host-ability"));
            Assert.That(projected.TimeLeft, Is.EqualTo(TimeSpan.FromSeconds(9)));
            projected.ContextChain[0].SourceAbility.Id = "projection-mutated";
            Assert.That(RealTimeBuffReplicaStore.ProjectConfirmed(buff, projected)
                .ContextChain[0].SourceAbility.Id, Is.EqualTo("host-ability"));
        }
    }
}
