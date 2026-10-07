using System;
using System.Collections.Generic;
using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;
using FakeItEasy;
using AutoMapper;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.EntitySystem.Persistence.JsonUtility;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeBuffContextChainTests
    {
        private sealed class Node { public Node Parent; public string Id; }
        private static NetworkBuffContextState State(string id = "buff") => new()
        {
            BlueprintId = id, Params = new() { CasterLevel = 8, SpellLevel = 3, DC = 17 },
            Ranks = [8, 4], SharedValues = [2], Properties = [5], Direction = new(1, 0, 0)
        };

        [Test]
        public void NativeCaptureCopiesCalculatedArraysWithoutWorldOrRecalculation()
        {
            var context = (MechanicsContext)Activator.CreateInstance(typeof(MechanicsContext),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, new object[] { default(JsonConstructorMark) }, null);
            var blueprint = (Kingmaker.UnitLogic.Buffs.Blueprints.BlueprintBuff)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Kingmaker.UnitLogic.Buffs.Blueprints.BlueprintBuff));
            blueprint.AssetGuid = Kingmaker.Blueprints.BlueprintGuid.Parse("11111111111111111111111111111111");
            typeof(MechanicsContext).GetField("AssociatedBlueprint").SetValue(context, blueprint);
            var ranks = (int[])typeof(MechanicsContext).GetField("m_Ranks", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(context);
            ranks[0] = 37;
            var mapper = A.Fake<IRuntimeMapper>();
            A.CallTo(() => mapper.Map<NetworkAbilityParams>(A<object>._)).Returns(new NetworkAbilityParams { CasterLevel = 12, SpellLevel = 6 });
            var captured = RealTimeBuffContextCapture.Capture(context, mapper);
            Assert.That(captured.Count, Is.EqualTo(1));
            Assert.That(captured[0].Ranks[0], Is.EqualTo(37));
            Assert.That(captured[0].Params.SpellLevel, Is.EqualTo(6));
            Assert.That(captured[0].MainTarget, Is.Null);
            ranks[0] = 99;
            Assert.That(captured[0].Ranks[0], Is.EqualTo(37), "Snapshot must not retain mutable host arrays.");
            captured[0].Ranks[0] = 15;
            var parameters = (Kingmaker.UnitLogic.Abilities.AbilityParams)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(Kingmaker.UnitLogic.Abilities.AbilityParams));
            typeof(Kingmaker.UnitLogic.Abilities.AbilityParams).GetProperty("DC").GetSetMethod(true)
                .Invoke(parameters, new object[] { 23 });
            RealTimeBuffContextReplication.Apply(context, captured, _ => parameters);
            Assert.That(ranks[0], Is.EqualTo(15));
            Assert.That(context.Params.DC, Is.EqualTo(23));
            captured[0].Ranks.Add(0);
            Assert.That(RealTimeBuffContextReplication.Validate(context, captured), Is.EqualTo("buff-context-array-shape-mismatch"));
            Assert.Throws<InvalidOperationException>(() => RealTimeBuffContextReplication.Apply(context, captured,
                _ => throw new InvalidOperationException("must-not-convert-invalid-context")));
            Assert.That(ranks[0], Is.EqualTo(15));
        }

        [Test]
        public void CapturesChildBeforeParentsAndSamplesOnlyAfterTopologyValidation()
        {
            var child = new Node { Id = "child", Parent = new Node { Id = "parent" } };
            var capture = RealTimeBuffContextChain.Capture(child, node => node.Parent, node => State(node.Id));
            Assert.That(capture.ConvertAll(node => node.BlueprintId), Is.EqualTo(new[] { "child", "parent" }));
            var calls = 0;
            child.Parent.Parent = child;
            Assert.Throws<InvalidOperationException>(() => RealTimeBuffContextChain.Capture(child, node => node.Parent,
                node => { calls++; return State(); }));
            Assert.That(calls, Is.Zero);
        }

        [Test]
        public void RejectsDepthOverflowBeforeSampling()
        {
            var root = new Node(); var tail = root;
            for (var i = 0; i < RealTimeBuffContextChain.MaximumDepth; i++) tail = tail.Parent = new Node();
            var calls = 0;
            Assert.Throws<InvalidOperationException>(() => RealTimeBuffContextChain.Capture(root, node => node.Parent,
                node => { calls++; return State(); }));
            Assert.That(calls, Is.Zero);
        }

        [TestCase("null-node")]
        [TestCase("blueprint")]
        [TestCase("params")]
        [TestCase("ranks")]
        [TestCase("overflow")]
        [TestCase("direction")]
        [TestCase("ability")]
        [TestCase("target")]
        public void RejectsMalformedContextValues(string malformed)
        {
            var node = State(); var chain = new List<NetworkBuffContextState> { node };
            switch (malformed)
            {
                case "null-node": chain[0] = null; break;
                case "blueprint": node.BlueprintId = ""; break;
                case "params": node.Params = null; break;
                case "ranks": node.Ranks = null; break;
                case "overflow": node.Properties = new List<int>(new int[257]); break;
                case "direction": node.Direction.X = float.NaN; break;
                case "ability": node.IsAbilityContext = true; break;
                case "target": node.MainTarget = new(new(float.PositiveInfinity, 0, 0), null, null); break;
            }
            Assert.That(RealTimeBuffContextChain.Validate(chain), Is.Not.Null);
        }

        [TestCase("rank")]
        [TestCase("shared")]
        [TestCase("property")]
        [TestCase("dc")]
        [TestCase("level")]
        [TestCase("caster")]
        [TestCase("descriptor")]
        [TestCase("parent")]
        public void ContextDivergenceChangesCanonicalFingerprint(string change)
        {
            var node = State();
            var buff = new NetworkBuff { Id = "buff", ContextChain = [node] };
            var snapshot = new RealTimeCombatSnapshot
            {
                Combat = new() { Units = [new() { Id = "unit", BuffCollection = new() { Buffs = [buff] } }] }
            };
            var baseline = RealTimeCombatHasher.Compute(snapshot);
            switch (change)
            {
                case "rank": node.Ranks[0]++; break;
                case "shared": node.SharedValues[0]++; break;
                case "property": node.Properties[0]++; break;
                case "dc": node.Params.DC++; break;
                case "level": node.Params.SpellLevel++; break;
                case "caster": node.CasterId = "another"; break;
                case "descriptor": node.SpellDescriptor++; break;
                case "parent": buff.ContextChain.Add(State("parent")); break;
            }
            Assert.That(RealTimeCombatHasher.Compute(snapshot), Is.Not.EqualTo(baseline));
        }
    }
}
