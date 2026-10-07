using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using Kingmaker.View;
using Kingmaker.View.MapObjects;
using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.HarmonyPatches.Combat;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeAreaViewRecoveryTests
    {
        private const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static void Set(EntityDataBase area, string field, object value) =>
            typeof(EntityDataBase).GetField(field, Fields).SetValue(area, value);
        private static object Data(AreaEffectView view) => typeof(EntityViewBase).GetField("m_Data", Fields).GetValue(view);
        private static AreaEffectView View(bool owned = true)
        {
            var view = (AreaEffectView)FormatterServices.GetUninitializedObject(typeof(AreaEffectView));
            if (owned) RealTimeAreaVisual.MarkOwned(view);
            return view;
        }
        private static AreaEffectEntityData Replica()
        {
            var source = RealTimeAreaReplicaFactoryTests.Source();
            return RealTimeAreaReplicaFactory.CreateDetached(source.Blueprint, source.Context, source.State, _ => null, _ => new());
        }
        private static AreaEffectEntityData Ordinary(string id)
        {
            var area = new AreaEffectEntityData(default(JsonConstructorMark));
            Set(area, "<UniqueId>k__BackingField", id);
            return area;
        }

        [Test]
        public void DiscoveryIncludesHiddenOwnedAreasButNotHiddenNativeAreas()
        {
            var active = Ordinary("active"); var hidden = Ordinary("hidden"); var replica = Replica();
            Assert.That(replica.IsInGame, Is.False);
            Assert.That(RealTimeAreaDiscovery.Collect(new[] { active }, new[] { active, hidden, replica }),
                Is.EqualTo(new[] { active, replica }));
        }

        [Test]
        public void DiscoveryDeduplicatesReferencesNotConflictingIdentities()
        {
            var replica = Replica();
            Assert.That(RealTimeAreaDiscovery.Collect(new[] { replica }, new[] { replica }).Count, Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => RealTimeAreaDiscovery.Collect(new[] { replica }, new[] { Replica() }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BindingMissingViewHasNoNativeCallbacksAndCanRollback(bool commit)
        {
            var area = Replica(); var view = View();
            using (var binding = new RealTimeAreaViewBinding(area, view))
            {
                Assert.That(area.View, Is.SameAs(view)); Assert.That(Data(view), Is.SameAs(area));
                if (commit) binding.Commit();
            }
            Assert.That(ReferenceEquals(area.View, view), Is.EqualTo(commit));
            Assert.That(ReferenceEquals(Data(view), area), Is.EqualTo(commit));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReplacementLeavesOldBindingAvailableUntilCommit(bool commit)
        {
            var area = Replica(); var old = View(); var next = View();
            using (var binding = new RealTimeAreaViewBinding(area, old)) binding.Commit();
            using (var binding = new RealTimeAreaViewBinding(area, next))
            {
                Assert.That(area.View, Is.SameAs(next)); Assert.That(Data(old), Is.SameAs(area));
                if (commit) binding.Commit();
            }
            Assert.That(area.View, Is.SameAs(commit ? next : old));
            Assert.That(ReferenceEquals(Data(next), area), Is.EqualTo(commit));
            Assert.That(Data(old), Is.SameAs(area)); // Renderer disposal, not binding, owns old cleanup.
        }

        [Test]
        public void UnownedPreviousViewIsNeverSilentlyDetached()
        {
            var area = Replica(); var old = View(false); var next = View();
            Set(area, "<View>k__BackingField", old);
            Assert.Throws<InvalidOperationException>(() => new RealTimeAreaViewBinding(area, next));
            Assert.That(area.View, Is.SameAs(old)); Assert.That(Data(next), Is.Null);
        }

        [Test]
        public void ViewAlreadyBoundToOtherDataIsRefused()
        {
            var owner = Replica(); var other = Replica(); var view = View();
            using (var binding = new RealTimeAreaViewBinding(owner, view)) binding.Commit();
            Assert.Throws<InvalidOperationException>(() => new RealTimeAreaViewBinding(other, view));
            Assert.That(other.View, Is.Null); Assert.That(Data(view), Is.SameAs(owner));
        }

        [Test]
        public void PreviousViewWithForeignOwnerCannotBeRetiredByReplacement()
        {
            var area = Replica(); var other = Replica(); var previous = View(); var next = View();
            using (var binding = new RealTimeAreaViewBinding(other, previous)) binding.Commit();
            Set(area, "<View>k__BackingField", previous);
            Assert.Throws<InvalidOperationException>(() => new RealTimeAreaViewBinding(area, next));
            Assert.That(Data(previous), Is.SameAs(other)); Assert.That(Data(next), Is.Null);
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        public void PresenceRestoresRawFlagsAndRollsBackExactly(bool inGame, bool suppressed, bool commit)
        {
            var area = Replica(); Set(area, "m_IsInGame", inGame); Set(area, "m_Suppressed", suppressed);
            using (var presence = new RealTimeAreaPresence(area))
            {
                Assert.That(area.IsInGame, Is.True); Assert.That(area.Suppressed, Is.False);
                if (commit) presence.Commit();
            }
            Assert.That(area.IsInGame, Is.EqualTo(commit || inGame));
            Assert.That(area.Suppressed, Is.EqualTo(!commit && suppressed));
        }

        [Test]
        public void NativeVisibilitySettersAreBlockedOnlyForOwnedReplicas()
        {
            Assert.That(RealTimeAreaReplicaStatePatches.Targets().Select(method => method.Name),
                Is.EquivalentTo(new[] { "set_IsInGame", "set_Suppressed" }));
            Assert.That(RealTimeAreaReplicaStatePatches.BeforeNativeStateChange(Replica()), Is.False);
            Assert.That(RealTimeAreaReplicaStatePatches.BeforeNativeStateChange(Ordinary("native")), Is.True);
            Assert.That(RealTimeAreaReplicaStatePatches.BeforeNativeStateChange(null), Is.True);
        }

        [Test]
        public void WrongCheckpointRollsBackPresenceAndViewTogether()
        {
            var area = Replica(); var old = View(); var next = View();
            using (var binding = new RealTimeAreaViewBinding(area, old)) binding.Commit();
            Set(area, "m_IsInGame", false); Set(area, "m_Suppressed", true);
            using (var transaction = new RealTimeBuffRestoreTransaction())
            {
                transaction.RestoreAreaPresence(area);
                transaction.BindAreaView(area, next);
                Assert.That(transaction.HasChanges, Is.True);
                var wrong = new NetworkCombatState(); wrong.AreaEffects.Add(RealTimeAreaReplicaFactoryTests.Source().State);
                Assert.Throws<InvalidOperationException>(() => transaction.VerifyAndCommit(new(), wrong));
            }
            Assert.That(area.View, Is.SameAs(old)); Assert.That(Data(next), Is.Null);
            Assert.That(area.IsInGame, Is.False); Assert.That(area.Suppressed, Is.True);
        }

        [Test]
        public void BindingOwnershipLossIsReportedBeforeCommit()
        {
            var area = Replica(); var view = View(); var binding = new RealTimeAreaViewBinding(area, view);
            Set(area, "<View>k__BackingField", null);
            Assert.Throws<InvalidOperationException>(() => binding.ValidateForCommit());
            Set(area, "<View>k__BackingField", view);
            binding.Dispose(); Assert.That(area.View, Is.Null);
        }
    }
}
