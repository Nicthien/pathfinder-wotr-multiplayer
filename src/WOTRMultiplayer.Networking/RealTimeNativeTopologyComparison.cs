using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking
{
    // Inventory comparison only; does not certify arbitrary component fields,
    // event subscriptions, command ledgers or native execution equivalence.
    public static class RealTimeNativeTopologyComparison
    {
        public const string AbilityRuntimeType = "Kingmaker.UnitLogic.Abilities.Ability";

        public static bool AllowsDuplicateFactIds(string runtimeType)
            => string.Equals(runtimeType, AbilityRuntimeType, StringComparison.Ordinal);

        public static void RequireValid(NetworkNativeTopology topology)
        {
            if (topology?.Entities == null || topology.Entities.Count > 32768) throw Invalid();
            var budget = 262144; var ids = new HashSet<string>(StringComparer.Ordinal);
            void Node() { if (--budget < 0) throw Invalid(); }
            bool Text(string value, int maximum, bool empty = false) => value != null
                && value.Length <= maximum && (empty || !string.IsNullOrWhiteSpace(value));
            foreach (var entity in topology.Entities)
            {
                Node();
                if (entity == null || !Text(entity.Id, 256) || !ids.Add(entity.Id)
                    || entity.Kind != "unit" && entity.Kind != "area" || entity.Parts == null || entity.Facts == null) throw Invalid();
                foreach (var part in entity.Parts) { Node(); if (!Text(part, 2048)) throw Invalid(); }
                var facts = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var fact in entity.Facts)
                {
                    Node();
                    if (fact == null || !Text(fact.Id, 256) || !Text(fact.RuntimeType, 2048)
                        || !Text(fact.BlueprintId, 64) || fact.Components == null) throw Invalid();
                    if (facts.TryGetValue(fact.Id, out var previousRuntimeType)
                        && (previousRuntimeType != fact.RuntimeType || !AllowsDuplicateFactIds(fact.RuntimeType)))
                        throw Invalid();
                    facts[fact.Id] = fact.RuntimeType;
                    foreach (var component in fact.Components)
                    {
                        Node();
                        if (component == null || !Text(component.RuntimeType, 2048)
                            || !Text(component.SourceName, 2048, empty: true)) throw Invalid();
                    }
                }
            }
        }
        private static InvalidDataException Invalid() => new("native-topology-invalid-or-limit");

        public static string Difference(NetworkNativeTopology expected, NetworkNativeTopology actual)
        {
            RequireValid(expected); RequireValid(actual);
            var left = expected.Entities.OrderBy(entity => entity.Id, StringComparer.Ordinal).ToArray();
            var right = actual.Entities.OrderBy(entity => entity.Id, StringComparer.Ordinal).ToArray();
            if (left.Length != right.Length)
            {
                var leftIds = new HashSet<string>(left.Select(entity => entity.Id), StringComparer.Ordinal);
                var rightIds = new HashSet<string>(right.Select(entity => entity.Id), StringComparer.Ordinal);
                var missing = leftIds.Except(rightIds, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).Take(8);
                var extra = rightIds.Except(leftIds, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).Take(8);
                return $"entities-count:expected={left.Length};actual={right.Length};missing={string.Join(",", missing)};extra={string.Join(",", extra)}";
            }
            for (var index = 0; index < left.Length; index++)
            {
                var a = left[index]; var b = right[index]; var prefix = a.Id + ":";
                if (a.Id != b.Id) return prefix + "entity-id";
                if (a.Kind != b.Kind) return prefix + "entity-kind";
                // EntityPartsManager may rebuild its internal list during the native
                // PreSave/PostSave lifecycle.  Lookup is type based, so the list order is
                // not part of the native topology; the types and their multiplicities are.
                var expectedParts = a.Parts.OrderBy(part => part, StringComparer.Ordinal).ToList();
                var actualParts = b.Parts.OrderBy(part => part, StringComparer.Ordinal).ToList();
                if (!expectedParts.SequenceEqual(actualParts, StringComparer.Ordinal))
                {
                    var unmatched = new List<string>(actualParts);
                    var missingParts = new List<string>();
                    foreach (var part in expectedParts)
                    {
                        var match = unmatched.FindIndex(value => string.Equals(value, part, StringComparison.Ordinal));
                        if (match < 0) missingParts.Add(part); else unmatched.RemoveAt(match);
                    }
                    return prefix + "parts-types:missing=" + string.Join(",", missingParts.Take(8))
                        + ";extra=" + string.Join(",", unmatched.Take(8));
                }
                string ComponentSignature(NetworkNativeComponentTopology component) =>
                    component.RuntimeType + "@" + component.SourceName;
                string FactSignature(NetworkNativeFactTopology fact) => fact.RuntimeType + "@" + fact.BlueprintId
                    + "[" + string.Join("|", fact.Components.Select(ComponentSignature)
                        .OrderBy(value => value, StringComparer.Ordinal)) + "]";
                var expectedFacts = a.Facts.Select(FactSignature).OrderBy(value => value, StringComparer.Ordinal).ToList();
                var actualFacts = b.Facts.Select(FactSignature).OrderBy(value => value, StringComparer.Ordinal).ToList();
                if (!expectedFacts.SequenceEqual(actualFacts, StringComparer.Ordinal))
                {
                    var unmatchedFacts = new List<string>(actualFacts);
                    var missingFacts = new List<string>();
                    foreach (var fact in expectedFacts)
                    {
                        var match = unmatchedFacts.FindIndex(value => string.Equals(value, fact, StringComparison.Ordinal));
                        if (match < 0) missingFacts.Add(fact); else unmatchedFacts.RemoveAt(match);
                    }
                    return prefix + $"facts-inventory:expected={expectedFacts.Count};actual={actualFacts.Count};missing={string.Join(",", missingFacts.Take(4))};extra={string.Join(",", unmatchedFacts.Take(4))}";
                }
            }
            return null;
        }
    }
}
