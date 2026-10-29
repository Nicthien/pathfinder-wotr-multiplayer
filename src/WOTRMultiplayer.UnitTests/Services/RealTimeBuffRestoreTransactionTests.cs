using System;
using System.Linq;
using System.Runtime.Serialization;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Buffs;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using Kingmaker.UnitLogic.Mechanics;
using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeBuffRestoreTransactionTests
    {
        private static T Bare<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
        private static EntityFactsManager Manager() => new(Bare<UnitEntityData>());
        private static NetworkBuff State(string id = "buff") => new()
        {
            Id = id, BlueprintId = "11111111111111111111111111111111", Rank = 2,
            TimeLeft = TimeSpan.FromSeconds(10), NextTickTime = TimeSpan.MaxValue,
            NextResourceSpendingTime = TimeSpan.MaxValue
        };
        private static Buff Replica(string id = "buff")
        {
            var blueprint = Bare<BlueprintBuff>(); blueprint.AssetGuid = BlueprintGuid.Parse(State().BlueprintId);
            return RealTimeBuffReplicaStore.CreateDetached(blueprint, Bare<MechanicsContext>(), State(id), TimeSpan.Zero);
        }
        private static NetworkCombatState Checkpoint() => new()
        {
            Units = [new() { Id = "hero", BuffCollection = new() { Buffs = [State()] } }]
        };

        [Test]
        public void VerifiedCheckpointRetainsRegistrationsWithoutMechanicalActivation()
        {
            var manager = Manager(); var buff = Replica(); var refreshes = 0;
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                transaction.Register(manager, buff, () => refreshes++);
                transaction.VerifyAndCommit(Checkpoint(), Checkpoint());
            }
            Assert.That(manager.GetAll<Buff>().Single(), Is.SameAs(buff));
            Assert.That(buff.IsActive || buff.IsSubscribedOnEventBus, Is.False);
            Assert.That(RealTimeBuffReplicaStore.RequiresMechanicalRebuild(manager), Is.True,
                "A presentation-only fact must never count as a mechanically reconstructed buff.");
            Assert.That(refreshes, Is.Zero);
        }

        [Test]
        public void ReplicaCallbacksStayBlockedWithoutAnActiveMultiplayerSession()
        {
            Assert.That(WOTRMultiplayer.HarmonyPatches.Combat.RealTimeBuffReplicaLifecyclePatches.Prepare(),
                Is.EqualTo(RealTimePresentationRuntime.Enabled));
            Assert.That(WOTRMultiplayer.HarmonyPatches.Combat.RealTimeBuffReplicaLifecyclePatches.Targets()
                .Select(method => method.Name), Is.EquivalentTo(new[] { "TickMechanics", "OnRemove", "Remove", "RemoveRank", "RemoveAfterDelay" }));
            Assert.That(WOTRMultiplayer.HarmonyPatches.Combat.RealTimeBuffReplicaLifecyclePatches.BeforeNativeMutation(Replica()), Is.False);
            Assert.That(WOTRMultiplayer.HarmonyPatches.Combat.RealTimeBuffReplicaLifecyclePatches.BeforeNativeMutation(Bare<Buff>()), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HashOrTimerMismatchRollsBackNewFacts(bool timer)
        {
            var manager = Manager(); var buff = Replica(); var refreshes = 0;
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                transaction.Register(manager, buff, () => { Assert.That(manager.GetAll<Buff>(), Is.Empty); refreshes++; });
                var actual = Checkpoint();
                if (timer) actual.Units[0].BuffCollection.Buffs[0].TimeLeft += TimeSpan.FromSeconds(3);
                else actual.Units[0].Orientation = 90;
                Assert.Throws<InvalidOperationException>(() => transaction.VerifyAndCommit(Checkpoint(), actual));
            }
            Assert.That(buff.Manager, Is.Null);
            Assert.That(manager.GetAll<Buff>(), Is.Empty);
            Assert.That(RealTimeBuffReplicaStore.RequiresMechanicalRebuild(manager), Is.False);
            Assert.That(refreshes, Is.EqualTo(1));
        }

        [Test]
        public void FailureOnAnotherUnitRemovesEveryNewRegistrationBeforeRefreshingSchedulers()
        {
            var first = Manager(); var second = Manager();
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                void Refresh() { Assert.That(first.GetAll<Buff>(), Is.Empty); Assert.That(second.GetAll<Buff>(), Is.Empty); }
                transaction.Register(first, Replica(), Refresh);
                transaction.Register(second, Replica(), Refresh);
                Assert.Throws<InvalidOperationException>(() => transaction.Register(second, Replica(), Refresh));
            }
            Assert.That(first.GetAll<Buff>(), Is.Empty);
            Assert.That(second.GetAll<Buff>(), Is.Empty);
        }

        [Test]
        public void SchedulerFailureStillAttemptsOtherSchedulersAndReportsFailure()
        {
            var manager = Manager(); var refreshed = false; var transaction = new RealTimeBuffRestoreTransaction();
            transaction.Register(manager, Replica(), () => throw new InvalidOperationException("scheduler"));
            transaction.Register(manager, Replica("second"), () => refreshed = true);
            Assert.Throws<AggregateException>(() => transaction.Dispose());
            Assert.That(refreshed, Is.True); Assert.That(manager.GetAll<Buff>(), Is.Empty);
            Assert.DoesNotThrow(() => transaction.Dispose());
        }

        [Test]
        public void CommittedOrDisposedTransactionsCannotAcceptMoreFacts()
        {
            var transaction = new RealTimeBuffRestoreTransaction();
            transaction.VerifyAndCommit(Checkpoint(), Checkpoint());
            Assert.Throws<InvalidOperationException>(() => transaction.Register(Manager(), Replica(), null));
            transaction.Dispose();
            Assert.Throws<ObjectDisposedException>(() => transaction.VerifyAndCommit(Checkpoint(), Checkpoint()));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeStateMutationRollsBackUnlessCheckpointCommits(bool commit)
        {
            var value = 2;
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                value = 7;
                transaction.TrackNativeState(
                    () =>
                    {
                        if (value != 7) throw new InvalidOperationException("changed");
                    },
                    () => value = 2);
                if (commit) transaction.VerifyAndCommit(new(), new());
            }
            Assert.That(value, Is.EqualTo(commit ? 7 : 2));
        }

        [Test]
        public void NativeStateValidationFailureStillRollsBack()
        {
            var value = 3;
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                value = 9;
                transaction.TrackNativeState(
                    () => throw new InvalidOperationException("changed"),
                    () => value = 3);
                Assert.Throws<InvalidOperationException>(() => transaction.VerifyAndCommit(new(), new()));
            }
            Assert.That(value, Is.EqualTo(3));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AuthoritativeRemovalIsRetainedOnlyAfterVerification(bool verified)
        {
            var manager = Manager(); var buff = Replica();
            using (var registration = RealTimeBuffReplicaStore.Register(manager, buff)) registration.Commit();
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                transaction.Remove(manager, buff, null);
                Assert.That(manager.GetAll<Buff>(), Is.Empty);
                if (verified) transaction.VerifyAndCommit(new(), new());
                else Assert.Throws<InvalidOperationException>(() => transaction.VerifyAndCommit(Checkpoint(), new()));
            }
            Assert.That(manager.GetAll<Buff>().Contains(buff), Is.EqualTo(!verified));
            Assert.That(ReferenceEquals(buff.Manager, manager), Is.EqualTo(!verified));
            Assert.That(buff.IsActive || buff.IsSubscribedOnEventBus, Is.False);
        }

        [Test]
        public void FailedReplacementRemovesNewReplicaAndRestoresOldReplica()
        {
            var manager = Manager(); var old = Replica("old"); var replacement = Replica("new");
            using (var registration = RealTimeBuffReplicaStore.Register(manager, old)) registration.Commit();
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                transaction.Remove(manager, old, null);
                transaction.Register(manager, replacement, null);
            }
            Assert.That(manager.GetAll<Buff>().Single(), Is.SameAs(old));
            Assert.That(replacement.Manager, Is.Null);
        }

        [Test]
        public void RawRemovalNeverAcceptsAnOrdinaryMechanicalBuff()
        {
            var manager = Manager();
            Assert.Throws<InvalidOperationException>(() => RealTimeBuffReplicaStore.RemoveReplica(manager, Bare<Buff>()));
            Assert.That(manager.GetAll<Buff>(), Is.Empty);
        }

        [Test]
        public void RestorePlanMatchesInstancesNotBlueprintAndDoesNotChangeInputCollections()
        {
            var first = State("first"); var missing = State("second");
            missing.ContextChain = [new() { BlueprintId = missing.BlueprintId, Params = new(), Direction = new() }];
            var local = new NetworkUnitBuffCollection { Buffs = [first] };
            var expected = new NetworkUnitBuffCollection { Buffs = [first, missing] };
            var plan = RealTimeBuffState.MissingForPresentationRestore(expected, local);
            Assert.That(plan.Single(), Is.SameAs(missing));
            Assert.That(local.Buffs.Count, Is.EqualTo(1));
            Assert.That(expected.Buffs.Count, Is.EqualTo(2));
        }

        [Test]
        public void RestorePlanAcceptsContextlessNativeBuffEncodedAsEmptyRepeatedField()
        {
            var missing = State("contextless");
            missing.ContextChain = [];
            var expected = new NetworkUnitBuffCollection { Buffs = [missing] };
            var local = new NetworkUnitBuffCollection();

            Assert.That(RealTimeBuffState.MissingForPresentationRestore(expected, local),
                Is.EqualTo(new[] { missing }));
        }

        [TestCase("extra")]
        [TestCase("duplicate")]
        [TestCase("context")]
        [TestCase("root-blueprint")]
        [TestCase("caster")]
        public void RestorePlanRejectsUnsupportedCollectionsBeforeCreatingAnything(string reason)
        {
            var first = State("first"); var missing = State("second");
            missing.ContextChain = [new() { BlueprintId = missing.BlueprintId, Params = new(), Direction = new() }];
            var local = new NetworkUnitBuffCollection { Buffs = [State("first")] };
            var expected = new NetworkUnitBuffCollection { Buffs = [first, missing] };
            switch (reason)
            {
                case "extra": local.Buffs.Add(State("extra")); break;
                case "duplicate": expected.Buffs.Add(missing); break;
                case "context": missing.ContextChain = null; break;
                case "root-blueprint": missing.ContextChain[0].BlueprintId = "different"; break;
                case "caster": first.CasterId = "different"; break;
            }
            Assert.Throws<InvalidOperationException>(() => RealTimeBuffState.MissingForPresentationRestore(expected, local));
        }
    }
}
