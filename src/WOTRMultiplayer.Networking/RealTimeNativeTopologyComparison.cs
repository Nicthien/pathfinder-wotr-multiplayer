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
                var facts = new HashSet<string>(StringComparer.Ordinal);
                foreach (var fact in entity.Facts)
                {
                    Node();
                    if (fact == null || !Text(fact.Id, 256) || !facts.Add(fact.Id) || !Text(fact.RuntimeType, 2048)
                        || !Text(fact.BlueprintId, 64) || fact.Components == null) throw Invalid();
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
            if (left.Length != right.Length) return "entities-count";
            for (var index = 0; index < left.Length; index++)
            {
                var a = left[index]; var b = right[index]; var prefix = a.Id + ":";
                if (a.Id != b.Id) return prefix + "entity-id";
                if (a.Kind != b.Kind) return prefix + "entity-kind";
                if (!a.Parts.SequenceEqual(b.Parts, StringComparer.Ordinal)) return prefix + "parts-order-or-types";
                if (a.Facts.Count != b.Facts.Count) return prefix + "facts-count";
                for (var factIndex = 0; factIndex < a.Facts.Count; factIndex++)
                {
                    var x = a.Facts[factIndex]; var y = b.Facts[factIndex]; var factPrefix = prefix + x.Id + ":";
                    if (x.Id != y.Id || x.RuntimeType != y.RuntimeType || x.BlueprintId != y.BlueprintId)
                        return factPrefix + "fact-order-type-or-blueprint";
                    if (x.Components.Count != y.Components.Count) return factPrefix + "components-count";
                    for (var component = 0; component < x.Components.Count; component++)
                        if (x.Components[component].RuntimeType != y.Components[component].RuntimeType
                            || x.Components[component].SourceName != y.Components[component].SourceName)
                            return factPrefix + "component-order-type-or-source:" + component;
                }
            }
            return null;
        }
    }
}
