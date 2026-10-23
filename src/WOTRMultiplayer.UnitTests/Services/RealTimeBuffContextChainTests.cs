using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;
using FakeItEasy;
using AutoMapper;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
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
        public void NativeCheckpointCanNormalizeAConfirmedPointTargetBeforeValidation()
        {
            var context = (MechanicsContext)Activator.CreateInstance(typeof(MechanicsContext),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, new object[] { default(JsonConstructorMark) }, null);
            var state = State();
            state.MainTarget = new(new(1, 2, 3), 45, null);

            RealTimeBuffContextReplication.ApplyConfirmedTargets(context, new[] { state }, _ => null);

            Assert.That(context.MainTarget.Point.x, Is.EqualTo(1));
            Assert.That(context.MainTarget.Point.y, Is.EqualTo(2));
            Assert.That(context.MainTarget.Point.z, Is.EqualTo(3));
            Assert.That(context.MainTarget.Orientation, Is.EqualTo(45));
        }

        [Test]
        public void CaptureSurvivesMissingTransientRuntimeAbilityReference()
        {
            var context = (AbilityExecutionContext)Activator.CreateInstance(typeof(AbilityExecutionContext),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, new object[] { default(JsonConstructorMark) }, null);
            var blueprint = (BlueprintAbility)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(BlueprintAbility));
            blueprint.AssetGuid = Kingmaker.Blueprints.BlueprintGuid.Parse("22222222222222222222222222222222");
            typeof(MechanicsContext).GetField("AssociatedBlueprint").SetValue(context, blueprint);
            var mapper = A.Fake<IRuntimeMapper>();
            A.CallTo(() => mapper.Map<NetworkAbilityParams>(A<object>._)).Returns(new NetworkAbilityParams());

            var runtimeCapture = RealTimeBuffContextCapture.Capture(context, mapper);
            Assert.That(runtimeCapture.Single().SourceAbility.BlueprintId,
                Is.EqualTo("22222222222222222222222222222222"));
            Assert.That(runtimeCapture.Single().SourceAbility.Id, Is.Null);
            using (new CombatSaveDigestScope())
            {
                var captured = RealTimeBuffContextCapture.Capture(context, mapper);
                Assert.That(captured.Single().SourceAbility.BlueprintId,
                    Is.EqualTo("22222222222222222222222222222222"));
                Assert.That(captured.Single().SourceAbility.Id, Is.Null);
            }
        }

        [Test]
        public void AppliedHostAbilityProjectionSurvivesMissingTransientRuntimeAbilityReference()
        {
            var context = (AbilityExecutionContext)Activator.CreateInstance(typeof(AbilityExecutionContext),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, new object[] { default(JsonConstructorMark) }, null);
            var blueprint = (BlueprintAbility)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(BlueprintAbility));
            blueprint.AssetGuid = Kingmaker.Blueprints.BlueprintGuid.Parse("44444444444444444444444444444444");
            typeof(MechanicsContext).GetField("AssociatedBlueprint").SetValue(context, blueprint);
            var mapper = A.Fake<IRuntimeMapper>();
            A.CallTo(() => mapper.Map<NetworkAbilityParams>(A<object>._)).Returns(new NetworkAbilityParams());
            var expected = RealTimeBuffContextCapture.Capture(context, mapper);
            expected.Single().SourceAbility = new WOTRMultiplayer.Entities.Combat.NetworkAbility
            {
                Id = "host-action", Name = "SunderAction",
                BlueprintId = blueprint.AssetGuid.ToString(), SpellLevel = 2
            };
            var parameters = (AbilityParams)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(AbilityParams));

            RealTimeBuffContextReplication.Apply(context, expected, _ => parameters);
            expected.Single().SourceAbility.Id = "caller-mutated";
            var captured = RealTimeBuffContextCapture.Capture(context, mapper).Single().SourceAbility;

            Assert.That(captured.Id, Is.EqualTo("host-action"));
            Assert.That(captured.Name, Is.EqualTo("SunderAction"));
            Assert.That(captured.SpellLevel, Is.EqualTo(2));
            Assert.That(captured.BlueprintId, Is.EqualTo(blueprint.AssetGuid.ToString()));
        }

        [Test]
        public void RuntimeAndCombatSaveValidationUseSourceBlueprintWithoutRuntimeAbilityReference()
        {
            var context = (AbilityExecutionContext)Activator.CreateInstance(typeof(AbilityExecutionContext),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, new object[] { default(JsonConstructorMark) }, null);
            var blueprint = (BlueprintAbility)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(BlueprintAbility));
            blueprint.AssetGuid = Kingmaker.Blueprints.BlueprintGuid.Parse("33333333333333333333333333333333");
            typeof(MechanicsContext).GetField("AssociatedBlueprint").SetValue(context, blueprint);
            var mapper = A.Fake<IRuntimeMapper>();
            A.CallTo(() => mapper.Map<NetworkAbilityParams>(A<object>._)).Returns(new NetworkAbilityParams());
            List<NetworkBuffContextState> expected;
            using (new CombatSaveDigestScope())
                expected = RealTimeBuffContextCapture.Capture(context, mapper);

            Assert.That(RealTimeBuffContextReplication.Validate(context, expected), Is.Null);
            using (new CombatSaveDigestScope())
                Assert.That(RealTimeBuffContextReplication.Validate(context, expected), Is.Null);
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
        public void EmptyWireChainRepresentsAContextlessNativeBuff()
        {
            var states = new List<NetworkBuffContextState>();
            Assert.That(RealTimeBuffContextChain.Validate(states), Is.Null);
            Assert.That(RealTimeBuffContextFactory.Create(states, _ => null, _ => null, _ => null,
                _ => null, _ => null), Is.Null);
            Assert.That(RealTimeBuffContextReplication.Validate(null, states), Is.Null);
            Assert.DoesNotThrow(() => RealTimeBuffContextReplication.Apply(null, states, _ => null));
        }

        [Test]
        public void AssociatedBlueprintMayDifferFromExecutionAbilityWithoutLosingEitherReference()
        {
            var node = State("associated-blueprint");
            node.IsAbilityContext = true;
            node.SourceAbility = new WOTRMultiplayer.Entities.Combat.NetworkAbility { BlueprintId = "executed-ability" };
            Assert.That(RealTimeBuffContextChain.Validate(new[] { node }), Is.Null);
            node.SourceAbility.BlueprintId = null;
            Assert.That(RealTimeBuffContextChain.Validate(new[] { node }), Is.EqualTo("invalid-buff-context-ability"));
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
