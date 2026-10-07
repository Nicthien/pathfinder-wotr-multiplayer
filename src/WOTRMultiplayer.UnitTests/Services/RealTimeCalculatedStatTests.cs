using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AutoMapper;
using Kingmaker.EntitySystem.Stats;
using NUnit.Framework;
using WOTRMultiplayer.Config.Mapping;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeCalculatedStatTests
    {
        private static NetworkCalculatedStat State(StatType type = StatType.AC, int value = 17) => new()
        {
            Type = (int)type, Values = new() { BaseValue = 10, ModifiedValue = value, ModifiedValueRaw = value, PermanentValue = 12 }
        };

        [Test]
        public void NativeCaptureAndApplyCopyCachesWithoutCallbacksOrRecalculation()
        {
            var native = new ModifiableValue(null, StatType.AdditionalAttackBonus);
            var callbacks = 0;
            var changed = typeof(ModifiableValue).GetField("OnChanged", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Action<ModifiableValue, int> onChanged = (_, _) => callbacks++;
            changed.SetValue(native, Delegate.CreateDelegate(changed.FieldType, onChanged.Target, onChanged.Method));
            var expected = new[] { State(StatType.AdditionalAttackBonus, -3) };
            RealTimeCalculatedStatReplication.Apply(new[] { native }, expected);
            Assert.That(native.BaseValue, Is.EqualTo(10));
            Assert.That(native.ModifiedValue, Is.EqualTo(-3));
            Assert.That(native.ModifiedValueRaw, Is.EqualTo(-3));
            Assert.That(native.PermanentValue, Is.EqualTo(12));
            var captured = RealTimeCalculatedStatReplication.Capture(new[] { native });
            expected[0].Values.ModifiedValue = 99;
            Assert.That(captured[0].Values.ModifiedValue, Is.EqualTo(-3));
            Assert.That(callbacks, Is.Zero);
        }

        [TestCase("duplicate")]
        [TestCase("type")]
        [TestCase("values")]
        [TestCase("entry")]
        [TestCase("empty")]
        [TestCase("overflow")]
        public void InvalidStatsAreRejectedBeforeWrites(string issue)
        {
            var native = new ModifiableValue(null, StatType.AC);
            var expected = new List<NetworkCalculatedStat> { State() };
            switch (issue)
            {
                case "duplicate": expected.Add(State()); break;
                case "type": expected[0].Type = int.MaxValue; break;
                case "values": expected[0].Values = null; break;
                case "entry": expected[0] = null; break;
                case "empty": expected.Clear(); break;
                case "overflow": expected = Enumerable.Range(0, 257).Select(_ => State()).ToList(); break;
            }
            Assert.Throws<InvalidOperationException>(() => RealTimeCalculatedStatReplication.Apply(new[] { native }, expected));
            Assert.That(native.BaseValue, Is.Zero);
        }

        [Test]
        public void MissingNativeStatIsNotSubstitutedOrCreated()
        {
            var native = new ModifiableValue(null, StatType.AC);
            Assert.Throws<InvalidOperationException>(() => RealTimeCalculatedStatReplication.Apply(new[] { native },
                new[] { State(), State(StatType.Speed) }));
            Assert.That(native.BaseValue, Is.Zero);
        }

        [Test]
        public void CanonicalHashSortsStatsAndDetectsChangesInEveryCache()
        {
            ulong Hash(List<NetworkCalculatedStat> stats) => RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot
                { Combat = new() { Units = [new() { Id = "hero", Descriptor = new() { CalculatedStats = stats } }] } });
            var first = State(); var second = State(StatType.Speed); var baseline = Hash([first, second]);
            Assert.That(Hash([second, first]), Is.EqualTo(baseline));
            foreach (var field in new[] { "BaseValue", "ModifiedValue", "ModifiedValueRaw", "PermanentValue" })
            {
                first = State(); var property = typeof(NetworkStatValues).GetProperty(field);
                property.SetValue(first.Values, (int)property.GetValue(first.Values) + 1);
                Assert.That(Hash([first, second]), Is.Not.EqualTo(baseline), field);
            }
            Assert.That(Hash(null), Is.Not.EqualTo(baseline));
        }

        [Test]
        public void DivergenceDiagnosticNamesTheUnitAndCalculatedStats()
        {
            var expected = new WOTRMultiplayer.Entities.Combat.NetworkCombatState
                { Units = [new() { Id = "hero", Descriptor = new() { CalculatedStats = [State()] } }] };
            var actual = new WOTRMultiplayer.Entities.Combat.NetworkCombatState
                { Units = [new() { Id = "hero", Descriptor = new() { CalculatedStats = [State(value: 18)] } }] };
            Assert.That(RealTimeCombatDiff.Summarize(expected, actual), Is.EqualTo("unit:hero:calculated-stats"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingCaptureOnEitherPeerCannotPassValidation(bool expectedMissing)
        {
            Assert.That(RealTimeCalculatedStatState.Validate(expectedMissing ? null : new[] { State() },
                expectedMissing ? new[] { State() } : null), Is.EqualTo("missing-calculated-stats"));
        }

        [Test]
        public void VitalsMustMatchTheirDuplicateCalculatedStatEntries()
        {
            var values = new NetworkStatValues { BaseValue = 10, ModifiedValue = 12, ModifiedValueRaw = 12, PermanentValue = 10 };
            var vitals = new NetworkUnitVitals { HitPoints = values, TemporaryHitPoints = values, NonLethal = values };
            var stats = new List<NetworkCalculatedStat>
            {
                new() { Type = (int)StatType.HitPoints, Values = values },
                new() { Type = (int)StatType.TemporaryHitPoints, Values = values },
                new() { Type = (int)StatType.DamageNonLethal, Values = values }
            };
            Assert.That(RealTimeCalculatedStatState.ValidateVitals(stats, vitals), Is.Null);
            stats[0].Values = new();
            Assert.That(RealTimeCalculatedStatState.ValidateVitals(stats, vitals), Does.Contain("HitPoints"));
        }

        [Test]
        public void DurableResultInboxDeepCopiesCalculatedStatsAndRejectsConflictingReuse()
        {
            var mapper = new MapperConfiguration(config => config.AddProfile<NetworkMessagesProfile>()).CreateMapper();
            var state = new NetworkUnitDescriptor { CalculatedStats = [State()] };
            var frame = new NotifyRealTimeAuthoritativeEvent
            {
                Sequence = 1, EventId = "event", ActionId = "result", UnitId = "hero",
                Kind = (int)RealTimeAuthoritativeEventKind.UnitResult,
                UnitResult = new() { State = mapper.Map<Networking.Messages.Contracts.NetworkUnitDescriptor>(state) }
            };
            var inbox = new RealTimePresentationInbox(); Assert.That(inbox.Enqueue(frame), Is.True);
            frame.UnitResult.State.CalculatedStats[0].Values.ModifiedValue = 100;
            Assert.That(inbox.Enqueue(frame), Is.False);
            inbox.TryPeek(out var retained);
            Assert.That(retained.UnitResult.State.CalculatedStats[0].Values.ModifiedValue, Is.EqualTo(17));
        }
    }
}
