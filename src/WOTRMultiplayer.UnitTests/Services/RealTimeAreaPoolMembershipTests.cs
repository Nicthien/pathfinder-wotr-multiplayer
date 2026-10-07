using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using NUnit.Framework;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeAreaPoolMembershipTests
    {
        private static AreaEffectEntityData Replica()
        {
            var source = RealTimeAreaReplicaFactoryTests.Source();
            return RealTimeAreaReplicaFactory.CreateDetached(source.Blueprint, source.Context, source.State, _ => null, _ => new());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RegisteredReplicaIsEnumeratedWithoutSceneEvents(bool commit)
        {
            var area = Replica(); var pool = new List<AreaEffectEntityData>();
            using (var membership = new RealTimeAreaPoolMembership(pool, area, removed: false))
            {
                var enumerator = new EntityPoolEnumerator<AreaEffectEntityData>(pool);
                Assert.That(enumerator.MoveNext(), Is.True);
                Assert.That(enumerator.Current, Is.SameAs(area));
                Assert.That(area.IsTurnedOn, Is.False);
                if (commit) membership.Commit();
            }
            Assert.That(pool.Count, Is.EqualTo(commit ? 1 : 0));
            Assert.That(area.IsInGame, Is.EqualTo(commit));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemovalCanRestoreEnumeration(bool commit)
        {
            var area = Replica(); var pool = new List<AreaEffectEntityData>();
            using (var membership = new RealTimeAreaPoolMembership(pool, area, removed: false)) membership.Commit();
            using (var removal = new RealTimeAreaPoolMembership(pool, area, removed: true))
            {
                Assert.That(new EntityPoolEnumerator<AreaEffectEntityData>(pool).MoveNext(), Is.False);
                if (commit) removal.Commit();
            }
            Assert.That(pool.Count, Is.EqualTo(commit ? 0 : 1));
            Assert.That(area.IsInGame, Is.EqualTo(!commit));
        }

        [Test]
        public void DuplicateIdentityIsRefusedBeforeMutation()
        {
            var original = Replica(); var other = Replica(); var pool = new List<AreaEffectEntityData> { original };
            Assert.Throws<InvalidOperationException>(() => new RealTimeAreaPoolMembership(pool, other, removed: false));
            Assert.That(pool, Is.EqualTo(new[] { original })); Assert.That(other.IsInGame, Is.False);
        }

        [Test]
        public void TransactionRollsBackReplacementAndRestoresOldEnumeration()
        {
            var old = Replica(); var next = Replica(); var pool = new List<AreaEffectEntityData>();
            using (var membership = new RealTimeAreaPoolMembership(pool, old, removed: false)) membership.Commit();
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                transaction.TrackAreaPool(pool, old, removed: true);
                transaction.TrackAreaPool(pool, next, removed: false);
            }
            Assert.That(pool, Is.EqualTo(new[] { old }));
            Assert.That(old.IsInGame, Is.True); Assert.That(next.IsInGame, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MultipleNativePoolsShareOneInGameFlag(bool commit)
        {
            var area = Replica(); var areas = new List<AreaEffectEntityData>();
            var entities = new List<EntityDataBase>(); var suppressible = new List<EntityDataBase>();
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                transaction.TrackAreaPool(areas, area, removed: false);
                transaction.TrackAreaPool(entities, area, removed: false);
                transaction.TrackAreaPool(suppressible, area, removed: false);
                Assert.That(new EntityPoolEnumerator<EntityDataBase>(entities).MoveNext(), Is.True);
                if (commit) transaction.VerifyAndCommit(new(), new());
            }
            Assert.That(areas.Count, Is.EqualTo(commit ? 1 : 0));
            Assert.That(entities.Count, Is.EqualTo(commit ? 1 : 0));
            Assert.That(suppressible.Count, Is.EqualTo(commit ? 1 : 0));
            Assert.That(area.IsInGame, Is.EqualTo(commit));
        }
    }
}
