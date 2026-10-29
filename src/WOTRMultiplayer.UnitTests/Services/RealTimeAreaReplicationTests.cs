using System;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using NUnit.Framework;
using WOTRMultiplayer.Config.Mapping;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeAreaReplicationTests
    {
        private static T Read<T>(AreaEffectEntityData area, string field) => (T)typeof(AreaEffectEntityData)
            .GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic).GetValue(area);
        private static NetworkAreaEffect State() => new()
        {
            Id = "zone", BlueprintId = "area", Position = new(1, 2, 3), Orientation = 90,
            CreationGameTimeTicks = 1234, DurationTicks = 5678
        };

        private static NetworkBuffContextState Context() => new()
        {
            BlueprintId = "area", Params = new() { DC = 22 }, Direction = new(),
            Ranks = [4], SharedValues = [7], Properties = [2]
        };

        [Test]
        public void RawAreaDataAppliesWithoutWorldViewOrCallbacks()
        {
            var area = new AreaEffectEntityData(default(JsonConstructorMark));
            var state = State();
            RealTimeAreaReplication.Apply(area, state, _ => throw new Exception("no occupants to resolve"),
                _ => throw new Exception("no context to convert"));
            Assert.That(Read<UnityEngine.Vector3>(area, "m_Position").x, Is.EqualTo(1));
            Assert.That(Read<UnityEngine.Vector3>(area, "m_Position").z, Is.EqualTo(3));
            Assert.That(Read<float>(area, "m_Rotation"), Is.EqualTo(90));
            Assert.That(Read<TimeSpan>(area, "m_CreationTime").Ticks, Is.EqualTo(1234));
            Assert.That(Read<TimeSpan?>(area, "m_Duration").Value.Ticks, Is.EqualTo(5678));
            Assert.That(Read<System.Collections.IEnumerable>(area, "m_UnitsInside"), Is.Empty);
            state.DurationTicks = null;
            RealTimeAreaReplication.Apply(area, state, _ => null, _ => null);
            Assert.That(Read<TimeSpan?>(area, "m_Duration"), Is.Null);
        }

        [TestCase("position")]
        [TestCase("orientation")]
        [TestCase("occupants")]
        [TestCase("duplicate-occupants")]
        [TestCase("context")]
        public void InvalidDataDoesNotPartiallyMutateArea(string change)
        {
            var area = new AreaEffectEntityData(default(JsonConstructorMark));
            var state = State();
            switch (change)
            {
                case "position": state.Position.X = float.NaN; break;
                case "orientation": state.Orientation = float.PositiveInfinity; break;
                case "occupants": state.UnitsInside = null; break;
                case "duplicate-occupants": state.UnitsInside = ["hero", "HERO"]; break;
                case "context": state.ContextChain = [Context()]; break;
            }
            Assert.Throws<InvalidOperationException>(() => RealTimeAreaReplication.Apply(area, state, _ => null, _ => null));
            Assert.That(Read<UnityEngine.Vector3>(area, "m_Position").x, Is.Zero);
            Assert.That(Read<TimeSpan>(area, "m_CreationTime").Ticks, Is.Zero);
        }

        [Test]
        public void MissingOccupantFailsBeforeNativeWrites()
        {
            var area = new AreaEffectEntityData(default(JsonConstructorMark));
            var state = State(); state.UnitsInside = ["missing"];
            Assert.Throws<InvalidOperationException>(() => RealTimeAreaReplication.Apply(area, state, _ => null, _ => null));
            Assert.That(Read<UnityEngine.Vector3>(area, "m_Position").x, Is.Zero);
            Assert.That(Read<System.Collections.IEnumerable>(area, "m_UnitsInside"), Is.Empty);
        }

        [Test]
        public void WireMappingPreservesOrientationAndCalculatedContext()
        {
            var mapper = new AutoMapper.MapperConfiguration(configuration =>
                configuration.AddProfile<NetworkMessagesProfile>()).CreateMapper();
            var state = State(); state.ContextChain = [Context()];
            var wire = mapper.Map<Networking.Messages.Contracts.NetworkAreaEffect>(state);
            var actual = mapper.Map<NetworkAreaEffect>(ProtoBuf.Serializer.DeepClone(wire));
            Assert.That(actual.Orientation, Is.EqualTo(90));
            Assert.That(actual.CreationGameTimeTicks, Is.EqualTo(1234));
            Assert.That(actual.DurationTicks, Is.EqualTo(5678));
            Assert.That(actual.ContextChain[0].Params.DC, Is.EqualTo(22));
            Assert.That(actual.ContextChain[0].Ranks.Single(), Is.EqualTo(4));
            Assert.That(actual.ContextChain[0].SharedValues.Single(), Is.EqualTo(7));
            Assert.That(actual.ContextChain[0].Properties.Single(), Is.EqualTo(2));
        }

        [TestCase("orientation")]
        [TestCase("rank")]
        [TestCase("shared")]
        [TestCase("dc")]
        [TestCase("context-presence")]
        public void AreaDivergenceChangesCanonicalHashAndDiagnostic(string change)
        {
            var expected = State(); expected.ContextChain = [Context()];
            var actual = State(); actual.ContextChain = [Context()];
            switch (change)
            {
                case "orientation": actual.Orientation++; break;
                case "rank": actual.ContextChain[0].Ranks[0]++; break;
                case "shared": actual.ContextChain[0].SharedValues[0]++; break;
                case "dc": actual.ContextChain[0].Params.DC++; break;
                case "context-presence": actual.ContextChain = null; break;
            }
            var host = new NetworkCombatState { AreaEffects = [expected] };
            var client = new NetworkCombatState { AreaEffects = [actual] };
            Assert.That(RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = host }),
                Is.Not.EqualTo(RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = client })));
            Assert.That(RealTimeCombatDiff.Summarize(host, client), Does.Contain(
                change == "orientation" ? "orientation" : "calculated-context"));
        }
    }
}
