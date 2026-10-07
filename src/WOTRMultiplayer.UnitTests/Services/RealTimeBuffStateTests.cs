using System;
using System.Runtime.Serialization;
using Kingmaker.UnitLogic.Buffs;
using NUnit.Framework;
using System.Linq;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeBuffStateTests
    {
        [Test]
        public void ScheduledBuffGuardsCoverNativeCollectionsAndArePrototypeOnly()
        {
            Assert.That(WOTRMultiplayer.HarmonyPatches.Combat.RealTimeBuffAuthorityPatches.Prepare(),
                Is.EqualTo(RealTimePresentationRuntime.Enabled));
            Assert.That(WOTRMultiplayer.HarmonyPatches.Combat.RealTimeBuffAuthorityPatches.Targets()
                .Select(method => method.Name), Is.EquivalentTo(new[] { "Tick", "Postpone", "UpdateOnDeadOwner" }));
        }
        private static NetworkBuff State(string id = "effect-1") => new()
        {
            Id = id, BlueprintId = "same-blueprint", CasterId = "caster", Rank = 1,
            TimeLeft = TimeSpan.FromSeconds(10), NextTickTime = TimeSpan.FromSeconds(2),
            NextResourceSpendingTime = TimeSpan.MaxValue
        };

        [Test]
        public void StackedBuffsMatchByIdentityRegardlessOfOrderingOrConfirmedRank()
        {
            var first = State(); first.Rank = 3;
            Assert.That(RealTimeBuffState.Validate(new[] { first, State("effect-2") },
                new[] { State("EFFECT-2"), State("EFFECT-1") }), Is.Null);
        }

        [TestCase("missing")]
        [TestCase("extra")]
        [TestCase("duplicate")]
        [TestCase("blueprint")]
        [TestCase("caster")]
        [TestCase("source-caster")]
        [TestCase("rank")]
        [TestCase("duration")]
        [TestCase("empty-id")]
        public void InvalidOrUnreconstructedEffectsFailBeforeApplication(string reason)
        {
            var expected = State(); var local = State();
            NetworkBuff[] host = { expected }, peer = { local };
            switch (reason)
            {
                case "missing": peer = Array.Empty<NetworkBuff>(); break;
                case "extra": peer = new[] { local, State("extra") }; break;
                case "duplicate": host = new[] { expected, State() }; break;
                case "blueprint": local.BlueprintId = "different"; break;
                case "caster": local.CasterId = "different"; break;
                case "source-caster": local.SourceAbilityCasterId = "different"; break;
                case "rank": expected.Rank = -1; break;
                case "duration": expected.TimeLeft = TimeSpan.FromTicks(-1); break;
                case "empty-id": expected.Id = ""; break;
            }
            Assert.That(RealTimeBuffState.Validate(host, peer), Is.Not.Null);
        }

        [Test]
        public void SameBlueprintCannotStandInForAnotherInstance()
        {
            Assert.That(RealTimeBuffState.Validate(new[] { State(), State("effect-2") },
                new[] { State() }), Is.EqualTo("missing-buff:effect-2"));
        }

        [Test]
        public void ConfirmedRankAndTimersApplyWithoutWorldBlueprintOwnerOrActivation()
        {
            // SetRank would consult the blueprint/reactivate components. None
            // of those dependencies exist on this intentionally bare object.
            var buff = (Buff)FormatterServices.GetUninitializedObject(typeof(Buff));
            var state = State(); state.Rank = 4;
            state.NextTickTime = TimeSpan.FromSeconds(-1);
            Assert.That(RealTimeBuffState.Validate(new[] { state }, new[] { State() }), Is.Null);
            RealTimeBuffReplication.ApplyValues(buff, state, TimeSpan.FromSeconds(100));
            Assert.That(buff.Rank, Is.EqualTo(4));
            Assert.That(buff.EndTime, Is.EqualTo(TimeSpan.FromSeconds(110)));
            Assert.That(typeof(Buff).GetProperty("NextTickTime", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(buff),
                Is.EqualTo(TimeSpan.FromSeconds(99)));
            Assert.That(buff.NextResourceSpendingTime, Is.EqualTo(TimeSpan.MaxValue));
            state.IsPermanent = true;
            RealTimeBuffReplication.ApplyValues(buff, state, TimeSpan.FromSeconds(200));
            Assert.That(buff.IsPermanent, Is.True);
        }

        [Test]
        public void InvalidValueDoesNotPartiallyChangeBuff()
        {
            var buff = (Buff)FormatterServices.GetUninitializedObject(typeof(Buff));
            var state = State(); state.Rank = -1;
            Assert.Throws<InvalidOperationException>(() => RealTimeBuffReplication.ApplyValues(buff, state, TimeSpan.Zero));
            Assert.That(buff.Rank, Is.Zero);
        }

        [TestCase("missing")]
        [TestCase("extra")]
        [TestCase("count")]
        [TestCase("permanent")]
        public void NegativeLevelStructureMustMatchBeforeUpdatingAnyBuff(string change)
        {
            var host = new NetworkUnitBuffCollection
            {
                NegativeLevels = { new() { Count = 1, Duration = TimeSpan.FromSeconds(10) } }
            };
            var local = new NetworkUnitBuffCollection
            {
                NegativeLevels = { new() { Count = 1, Duration = TimeSpan.FromSeconds(20) } }
            };
            Assert.That(RealTimeBuffState.ValidateCollection(host, local), Is.Null, "Timers alone can be repaired.");
            switch (change)
            {
                case "missing": local.NegativeLevels.Clear(); break;
                case "extra": host.NegativeLevels.Clear(); break;
                case "count": local.NegativeLevels[0].Count = 2; break;
                case "permanent": local.NegativeLevels[0].Duration = null; break;
            }
            Assert.That(RealTimeBuffState.ValidateCollection(host, local), Is.EqualTo("negative-level-structure-mismatch"));
        }
    }
}
