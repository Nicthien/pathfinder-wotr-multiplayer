using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using AutoMapper;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.UnitLogic;
using NUnit.Framework;
using WOTRMultiplayer.Config.Mapping;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeUnitResultTests
    {
        [Test]
        public void VitalGuardsAreInstalledOnlyInPrototypeBuild()
        {
            Assert.That(WOTRMultiplayer.HarmonyPatches.Combat.RealTimeUnitResultPatches.Prepare(),
                Is.EqualTo(RealTimePresentationRuntime.Enabled));
        }

        [Test]
        public void ConfirmedPartyCombatEndClearsNativeCachedFlagWithoutAWorld()
        {
            var player = (Kingmaker.Player)FormatterServices.GetUninitializedObject(typeof(Kingmaker.Player));
            typeof(Kingmaker.Player).GetProperty("IsInCombat").GetSetMethod(true).Invoke(player, new object[] { true });
            Assert.That(player.IsInCombat, Is.True);
            RealTimeUnitResultReplication.ApplyPartyCombatEnd(player);
            Assert.That(player.IsInCombat, Is.False);
        }

        private static NetworkStatValues Stat(int value) => new()
            { BaseValue = value, ModifiedValue = value, ModifiedValueRaw = value, PermanentValue = value };

        private static NetworkUnitDescriptor State(int damage = 5, int life = 0) => new()
        {
            Damage = damage, Stats = new() { DamageNonLethal = 2 }, State = new() { IsFinallyDead = life == 2 },
            ResourcesCaptured = true, SpellbooksCaptured = true,
            Vitals = new()
            {
                Damage = damage, LifeState = life, IsFinallyDead = life == 2,
                HitPoints = Stat(30), TemporaryHitPoints = Stat(3), NonLethal = Stat(2)
            }
        };

        private static UnitDescriptor Descriptor()
        {
            var descriptor = (UnitDescriptor)FormatterServices.GetUninitializedObject(typeof(UnitDescriptor));
            var statsField = typeof(UnitDescriptor).GetField("Stats");
            var stats = FormatterServices.GetUninitializedObject(statsField.FieldType);
            foreach (var name in new[] { "HitPoints", "TemporaryHitPoints", "DamageNonLethal" })
            {
                var field = stats.GetType().GetField(name);
                field.SetValue(stats, FormatterServices.GetUninitializedObject(field.FieldType));
            }
            statsField.SetValue(descriptor, stats);
            typeof(UnitDescriptor).GetField("State").SetValue(descriptor,
                FormatterServices.GetUninitializedObject(typeof(UnitState)));
            return descriptor;
        }

        private static int _callbacks;
        private static void StatChanged(ModifiableValue value, int old) { _callbacks++; }

        [Test]
        public void ActualGameFields_ApplyDeathAndHealthWithoutCalculationOrChangedCallbacks()
        {
            var descriptor = Descriptor();
            _callbacks = 0;
            var changed = typeof(ModifiableValue).GetField("OnChanged", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var handler = Delegate.CreateDelegate(changed.FieldType,
                typeof(RealTimeUnitResultTests).GetMethod(nameof(StatChanged), BindingFlags.Static | BindingFlags.NonPublic));
            changed.SetValue(descriptor.Stats.DamageNonLethal, handler);
            var expected = State(40, 2).Vitals;
            Assert.DoesNotThrow(() => RealTimeUnitResultReplication.ApplyValues(descriptor, expected));
            Assert.That(descriptor.Damage, Is.EqualTo(40));
            Assert.That(descriptor.State.IsDead, Is.True);
            Assert.That(descriptor.State.IsFinallyDead, Is.True);
            Assert.That(descriptor.Stats.HitPoints.ModifiedValue, Is.EqualTo(30));
            Assert.That(descriptor.Stats.TemporaryHitPoints.ModifiedValue, Is.EqualTo(3));
            Assert.That(descriptor.DamageNonLethal, Is.EqualTo(2));
            Assert.That(_callbacks, Is.Zero);
        }

        [Test]
        public void AuthoritativeHealingAndRevival_DoNotRecalculateLifeStateLocally()
        {
            var descriptor = Descriptor();
            RealTimeUnitResultReplication.ApplyValues(descriptor, State(40, 2).Vitals);
            var revived = State(3, 0).Vitals;
            RealTimeUnitResultReplication.ApplyValues(descriptor, revived);
            Assert.That(descriptor.State.IsConscious, Is.True);
            Assert.That(descriptor.State.IsFinallyDead, Is.False);
            Assert.That(descriptor.Damage, Is.EqualTo(3));
        }

        [Test]
        public void InvalidState_IsRejectedBeforeAnyFieldIsWritten()
        {
            var descriptor = Descriptor();
            RealTimeUnitResultReplication.ApplyValues(descriptor, State().Vitals);
            var invalid = State(99, 2).Vitals;
            invalid.NonLethal = null;
            Assert.Throws<InvalidOperationException>(() => RealTimeUnitResultReplication.ApplyValues(descriptor, invalid));
            Assert.That(descriptor.Damage, Is.EqualTo(5));
            Assert.That(descriptor.State.IsConscious, Is.True);
        }

        [TestCase("damage")]
        [TestCase("life")]
        [TestCase("nonlethal")]
        [TestCase("resources")]
        [TestCase("spellbooks")]
        public void ResultPreflight_RejectsInconsistentOrIncompletePayload(string problem)
        {
            var expected = State();
            switch (problem)
            {
                case "damage": expected.Damage = 99; break;
                case "life": expected.Vitals.LifeState = 99; break;
                case "nonlethal": expected.Stats.DamageNonLethal = 99; break;
                case "resources": expected.ResourcesCaptured = false; break;
                case "spellbooks": expected.SpellbooksCaptured = false; break;
            }
            Assert.That(RealTimeUnitResultState.Validate(expected, State()), Is.Not.Null);
        }

        [Test]
        public void ResourceTopologyFailure_IsDetectedBeforeHealthApplication()
        {
            var expected = State(20);
            expected.Resources.Add(new() { BlueprintId = "missing", Amount = 1, RetainCount = 1 });
            Assert.That(RealTimeUnitResultState.Validate(expected, State()), Does.StartWith("missing-resource:"));
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void IncompleteLocalCaptureCannotValidateAnAuthoritativeResult(bool resources, bool spellbooks)
        {
            var actual = State();
            actual.ResourcesCaptured = resources; actual.SpellbooksCaptured = spellbooks;
            Assert.That(RealTimeUnitResultState.Validate(State(), actual), Is.EqualTo("incomplete-unit-result"));
        }

        private static NotifyRealTimeAuthoritativeEvent Frame(long sequence, NetworkUnitDescriptor state)
        {
            var mapper = new MapperConfiguration(config => config.AddProfile<NetworkMessagesProfile>()).CreateMapper();
            return new()
            {
                Epoch = 2, Sequence = sequence, EventId = "2:" + sequence, UnitId = "unit", ActionId = "result:" + sequence,
                Kind = (int)RealTimeAuthoritativeEventKind.UnitResult,
                UnitResult = new() { State = mapper.Map<Networking.Messages.Contracts.NetworkUnitDescriptor>(state), Reason = "damage" }
            };
        }

        [Test]
        public void DuplicateAndReorderedDamageHealing_ApplyAbsoluteResultsOnceInOrder()
        {
            var inbox = new RealTimePresentationInbox();
            inbox.Enqueue(Frame(2, State(3)));
            inbox.Enqueue(Frame(1, State(20)));
            var calls = 0;
            var damage = 0;
            while (inbox.TryPeek(out var frame))
            {
                Assert.That(RealTimeAuthoritativePresentation.IsSupported(frame), Is.True);
                damage = frame.UnitResult.State.Vitals.Damage; calls++;
                inbox.Commit(frame.Sequence);
            }
            Assert.That(inbox.Enqueue(Frame(1, State(20))), Is.True);
            Assert.That(inbox.TryPeek(out _), Is.False);
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(damage, Is.EqualTo(3));
        }

        [Test]
        public void ReferenceBaseline_SkipsIncludedResultAndResumesNextDurableChange()
        {
            var inbox = new RealTimePresentationInbox();
            inbox.Enqueue(Frame(2, State(20)));
            inbox.Enqueue(Frame(3, State(3)));
            inbox.RestoreBaseline(2);
            Assert.That(inbox.TryPeek(out var next), Is.True);
            Assert.That(next.Sequence, Is.EqualTo(3));
            Assert.That(next.UnitResult.State.Vitals.Damage, Is.EqualTo(3));
        }

        [Test]
        public void PayloadIsCopiedAndConflictingReuseOfAnEventIsRejected()
        {
            var inbox = new RealTimePresentationInbox();
            var frame = Frame(1, State());
            Assert.That(inbox.Enqueue(frame), Is.True);
            frame.UnitResult.State.Vitals.Damage = 100;
            Assert.That(inbox.Enqueue(frame), Is.False);
            inbox.TryPeek(out var preserved);
            Assert.That(preserved.UnitResult.State.Vitals.Damage, Is.EqualTo(5));
        }

        [Test]
        public void UnitResult_CannotBeDispatchedAsAnAnimationOrAnAmbiguousEnvelope()
        {
            var frame = Frame(1, State());
            Assert.That(RealTimeAuthoritativePresentation.IsSupported(frame), Is.True);
            frame.Attack = new();
            Assert.That(RealTimeAuthoritativePresentation.IsSupported(frame), Is.False);
            frame.Attack = null; frame.Kind = (int)RealTimeAuthoritativeEventKind.AbilityPresentation;
            Assert.That(RealTimeAuthoritativePresentation.IsSupported(frame), Is.False);
        }

        [Test]
        public void CanonicalHash_IncludesLifeAndStatCachesAndMembership()
        {
            ulong Hash(NetworkUnitDescriptor state) => RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot
                { Combat = new NetworkCombatState { Units = [new() { Id = "unit", Descriptor = state }] } });
            var expected = State();
            var original = Hash(expected);
            expected.Vitals.LifeState = 1;
            Assert.That(Hash(expected), Is.Not.EqualTo(original));
            expected = State(); expected.Vitals.HitPoints.ModifiedValue = 22;
            Assert.That(Hash(expected), Is.Not.EqualTo(original));
            expected = State(); expected.Vitals.IsInCombat = true;
            Assert.That(Hash(expected), Is.Not.EqualTo(original));
        }
    }
}
