using System;
using System.IO;
using NUnit.Framework;
using ProtoBuf;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking.UnitTests
{
    public class RealTimeNativeTopologyComparisonTests
    {
        private static NetworkNativeTopology Source() => new()
        {
            Entities = new()
            {
                new() { Id = "unit", Kind = "unit", Parts = new() { "PartA", "PartB" }, Facts = new()
                {
                    new() { Id = "fact", RuntimeType = "Buff", BlueprintId = "blueprint", Components = new()
                    { new() { RuntimeType = "ComponentA", SourceName = "source-a" }, new() { RuntimeType = "ComponentB" } } },
                    new() { Id = "fact2", RuntimeType = "Feature", BlueprintId = "blueprint2" }
                } },
                new() { Id = "area", Kind = "area" }
            }
        };

        [Test]
        public void ReferenceCodecPreservesInventoryAndOwnsAllLists()
        {
            var state = new NetworkCombatState { NativeTopology = Source() };
            var bytes = RealTimeNativeReferenceCodec.Encode(state);
            var expected = Serializer.DeepClone(state.NativeTopology);
            state.NativeTopology.Entities[0].Facts[0].Components.Clear();
            var restored = RealTimeNativeReferenceCodec.Decode(bytes);
            Assert.That(RealTimeNativeTopologyComparison.Difference(expected, restored.NativeTopology), Is.Null);
            Assert.That(RealTimeNativeTopologyComparison.Difference(state.NativeTopology, restored.NativeTopology), Is.Not.Null);
        }

        [Test]
        public void EntityPoolEnumerationOrderDoesNotChangeInventory()
        {
            var expected = Source(); var actual = Serializer.DeepClone(expected); actual.Entities.Reverse();
            Assert.That(RealTimeNativeTopologyComparison.Difference(expected, actual), Is.Null);
            Assert.That(actual.Entities[0].Id, Is.EqualTo("area")); // Comparison does not sort caller lists in place.
        }

        [Test]
        public void EntityPartEnumerationOrderDoesNotChangeInventory()
        {
            var expected = Source(); var actual = Serializer.DeepClone(expected); actual.Entities[0].Parts.Reverse();
            Assert.That(RealTimeNativeTopologyComparison.Difference(expected, actual), Is.Null);
            Assert.That(actual.Entities[0].Parts, Is.EqualTo(new[] { "PartB", "PartA" }));
        }

        [Test]
        public void FactRuntimeIdsAndEnumerationOrderDoNotChangeInventory()
        {
            var expected = Source(); var actual = Serializer.DeepClone(expected);
            actual.Entities[0].Facts[0].Id = "regenerated-fact";
            actual.Entities[0].Facts.Reverse();
            actual.Entities[0].Facts[1].Components.Reverse();
            Assert.That(RealTimeNativeTopologyComparison.Difference(expected, actual), Is.Null);
        }

        [Test]
        public void DuplicateAbilityRuntimeIdsAreComparedBySemanticMultiplicity()
        {
            var expected = Source();
            var facts = expected.Entities[0].Facts;
            facts[0].RuntimeType = RealTimeNativeTopologyComparison.AbilityRuntimeType;
            facts[1].RuntimeType = RealTimeNativeTopologyComparison.AbilityRuntimeType;
            facts[1].Id = facts[0].Id;
            facts[1].BlueprintId = facts[0].BlueprintId;
            var actual = Serializer.DeepClone(expected);

            Assert.DoesNotThrow(() => RealTimeNativeTopologyComparison.RequireValid(actual));
            Assert.That(RealTimeNativeTopologyComparison.Difference(expected, actual), Is.Null);
            actual.Entities[0].Facts.RemoveAt(1);
            Assert.That(RealTimeNativeTopologyComparison.Difference(expected, actual), Does.Contain("facts-inventory"));
        }

        [Test]
        public void DuplicateAbilityIdCannotCollideWithAnotherFactType()
        {
            var topology = Source();
            var facts = topology.Entities[0].Facts;
            facts[0].RuntimeType = RealTimeNativeTopologyComparison.AbilityRuntimeType;
            facts[1].Id = facts[0].Id;

            Assert.Throws<InvalidDataException>(() => RealTimeNativeTopologyComparison.RequireValid(topology));
        }

        [TestCase("missing-entity")]
        [TestCase("extra-entity")]
        [TestCase("entity-id")]
        [TestCase("entity-kind")]
        [TestCase("missing-part")]
        [TestCase("missing-fact")]
        [TestCase("fact-type")]
        [TestCase("fact-blueprint")]
        [TestCase("missing-component")]
        [TestCase("component-type")]
        [TestCase("component-source")]
        public void MissingOrReplacedNativeObjectsCannotPassComparison(string fault)
        {
            var expected = Source(); var actual = Serializer.DeepClone(expected);
            var unit = actual.Entities[0]; var fact = unit.Facts[0];
            switch (fault)
            {
                case "missing-entity": actual.Entities.RemoveAt(1); break;
                case "extra-entity": actual.Entities.Add(new() { Id = "extra", Kind = "unit" }); break;
                case "entity-id": unit.Id = "other"; break;
                case "entity-kind": unit.Kind = "area"; break;
                case "missing-part": unit.Parts.RemoveAt(0); break;
                case "missing-fact": unit.Facts.RemoveAt(0); break;
                case "fact-type": fact.RuntimeType = "Other"; break;
                case "fact-blueprint": fact.BlueprintId = "other-blueprint"; break;
                case "missing-component": fact.Components.RemoveAt(0); break;
                case "component-type": fact.Components[0].RuntimeType = "OtherComponent"; break;
                case "component-source": fact.Components[0].SourceName = "other-source"; break;
            }
            Assert.That(RealTimeNativeTopologyComparison.Difference(expected, actual), Is.Not.Null);
        }

        [TestCase("null")]
        [TestCase("duplicate-entity")]
        [TestCase("duplicate-fact")]
        [TestCase("null-component")]
        [TestCase("long-type")]
        [TestCase("long-id")]
        [TestCase("unknown-kind")]
        public void InvalidInventoryIsRefusedBeforeReferenceSerialization(string fault)
        {
            var state = new NetworkCombatState { NativeTopology = Source() };
            var topology = state.NativeTopology;
            switch (fault)
            {
                case "null": topology.Entities = null; break;
                case "duplicate-entity": topology.Entities.Add(topology.Entities[0]); break;
                case "duplicate-fact": topology.Entities[0].Facts.Add(topology.Entities[0].Facts[0]); break;
                case "null-component": topology.Entities[0].Facts[0].Components.Add(null); break;
                case "long-type": topology.Entities[0].Parts[0] = new string('x', 2049); break;
                case "long-id": topology.Entities[0].Id = new string('x', 257); break;
                case "unknown-kind": topology.Entities[0].Kind = "view"; break;
            }
            Assert.Throws<InvalidDataException>(() => RealTimeNativeReferenceCodec.Encode(state));
        }
    }
}
