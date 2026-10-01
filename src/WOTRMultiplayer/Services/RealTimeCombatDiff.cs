using System;
using System.Collections.Generic;
using System.Linq;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services
{
    /// <summary>Small, bounded diagnostic summary using the same canonical projection as the checkpoint hash.</summary>
    public static class RealTimeCombatDiff
    {
        public static string Summarize(NetworkCombatState expected, NetworkCombatState actual)
        {
            if (expected == null || actual == null)
            {
                return "combat-state-missing";
            }

            var differences = new List<string>();
            var expectedUnits = Index(expected.Units, x => x.Id, "host-unit", differences);
            var actualUnits = Index(actual.Units, x => x.Id, "client-unit", differences);
            foreach (var id in expectedUnits.Keys.Union(actualUnits.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!expectedUnits.TryGetValue(id, out var hostUnit))
                {
                    differences.Add($"extra-unit:{id}");
                }
                else if (!actualUnits.TryGetValue(id, out var clientUnit))
                {
                    differences.Add($"missing-unit:{id}");
                }
                else if (!SameHash(new NetworkCombatState { Units = [hostUnit] },
                    new NetworkCombatState { Units = [clientUnit] }))
                {
                    differences.Add(DescribeUnitDifference(id, hostUnit, clientUnit));
                }
            }

            var expectedAreas = Index(expected.AreaEffects, x => x.Id, "host-area", differences);
            var actualAreas = Index(actual.AreaEffects, x => x.Id, "client-area", differences);
            foreach (var id in expectedAreas.Keys.Union(actualAreas.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!expectedAreas.TryGetValue(id, out var hostArea))
                {
                    differences.Add($"extra-area:{id}");
                }
                else if (!actualAreas.TryGetValue(id, out var clientArea))
                {
                    differences.Add($"missing-area:{id}");
                }
                else if (!SameHash(new NetworkCombatState { AreaEffects = [hostArea] },
                    new NetworkCombatState { AreaEffects = [clientArea] }))
                {
                    differences.Add(DescribeAreaDifference(id, hostArea, clientArea));
                }
            }

            if (differences.Count == 0)
            {
                return "unclassified-state-difference";
            }

            var visible = differences.Take(12).ToList();
            if (differences.Count > visible.Count)
            {
                visible.Add($"and-{differences.Count - visible.Count}-more");
            }
            return string.Join(",", visible);
        }

        private static ulong Hash(NetworkCombatState state) =>
            RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = state });

        private static bool SameHash(NetworkCombatState expected, NetworkCombatState actual)
        {
            try { return Hash(expected) == Hash(actual); }
            catch (InvalidOperationException) { return false; }
            catch (OverflowException) { return false; }
        }

        private static Dictionary<string, T> Index<T>(IEnumerable<T> values, Func<T, string> getId,
            string label, List<string> differences) where T : class
        {
            var indexed = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var value in values ?? [])
            {
                var id = value == null ? null : getId(value);
                if (id == null) continue;
                if (indexed.ContainsKey(id)) differences.Add($"duplicate-{label}:{id}");
                else indexed.Add(id, value);
            }
            return indexed;
        }

        private static string DescribeAreaDifference(string id,
            NetworkAreaEffect expected, NetworkAreaEffect actual)
        {
            var causes = new List<string>();
            if (!string.Equals(expected.BlueprintId, actual.BlueprintId, StringComparison.OrdinalIgnoreCase))
                causes.Add("blueprint");
            if (!string.Equals(expected.CasterUnitId, actual.CasterUnitId, StringComparison.Ordinal)
                || !string.Equals(expected.OwnerUnitId, actual.OwnerUnitId, StringComparison.Ordinal)
                || !string.Equals(expected.SourceAbilityBlueprintId, actual.SourceAbilityBlueprintId,
                    StringComparison.OrdinalIgnoreCase))
                causes.Add("context");
            if (!SameVector(expected.Position, actual.Position)) causes.Add("position");
            if (expected.Type != actual.Type) causes.Add("type");
            if (!(expected.UnitsInside ?? []).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual((actual.UnitsInside ?? []).OrderBy(x => x, StringComparer.Ordinal)))
                causes.Add("occupants");
            return causes.Count == 0 ? $"area:{id}" : $"area:{id}:{string.Join("+", causes)}";
        }

        private static string DescribeUnitDifference(string id, NetworkUnit expected, NetworkUnit actual)
        {
            var causes = new List<string>();
            if (!string.Equals(expected.BlueprintId, actual.BlueprintId, StringComparison.OrdinalIgnoreCase))
            {
                causes.Add("blueprint");
            }
            if (!string.Equals(expected.OriginalBlueprintId, actual.OriginalBlueprintId,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(expected.CasterUnitId, actual.CasterUnitId, StringComparison.Ordinal)
                || !string.Equals(expected.SourceAbilityBlueprintId, actual.SourceAbilityBlueprintId,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(expected.MasterUnitId, actual.MasterUnitId, StringComparison.Ordinal)
                || !string.Equals(expected.GroupId, actual.GroupId, StringComparison.Ordinal))
            {
                causes.Add("origin");
            }
            if (!SameVector(expected.Position, actual.Position)
                || !SameFloat(expected.Orientation, actual.Orientation))
                causes.Add("position");
            if ((expected.Descriptor?.Damage ?? 0) != (actual.Descriptor?.Damage ?? 0)
                || (expected.Descriptor?.Stats?.DamageNonLethal ?? 0)
                    != (actual.Descriptor?.Stats?.DamageNonLethal ?? 0))
                causes.Add("damage");
            if (!SameBuffs(expected.BuffCollection?.Buffs, actual.BuffCollection?.Buffs))
                causes.Add("buffs");
            var expectedProne = expected.Descriptor?.State?.Prone;
            var actualProne = actual.Descriptor?.State?.Prone;
            if ((expectedProne?.Active ?? false) != (actualProne?.Active ?? false)
                || (expectedProne?.ShouldBeActive ?? false) != (actualProne?.ShouldBeActive ?? false))
            {
                causes.Add("prone");
            }
            if (!SameEngagements(expected.CombatState?.EngagedUnits, actual.CombatState?.EngagedUnits)
                || !SameEngagements(expected.CombatState?.EngagedBy, actual.CombatState?.EngagedBy))
            {
                causes.Add("engagement");
            }
            return causes.Count == 0 ? $"unit:{id}" : $"unit:{id}:{string.Join("+", causes)}";
        }

        private static bool SameEngagements(IEnumerable<NetworkUnitEngagement> expected,
            IEnumerable<NetworkUnitEngagement> actual)
        {
            return (expected ?? []).Select(x => x?.UnitId).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual((actual ?? []).Select(x => x?.UnitId).OrderBy(x => x, StringComparer.Ordinal));
        }

        private static bool SameBuffs(IEnumerable<NetworkBuff> expected, IEnumerable<NetworkBuff> actual)
        {
            var left = (expected ?? []).OrderBy(x => x?.Id, StringComparer.Ordinal).ToArray();
            var right = (actual ?? []).OrderBy(x => x?.Id, StringComparer.Ordinal).ToArray();
            return left.Length == right.Length && left.Zip(right, (a, b) =>
                string.Equals(a?.Id, b?.Id, StringComparison.Ordinal)
                && string.Equals(a?.BlueprintId, b?.BlueprintId, StringComparison.Ordinal)
                && string.Equals(a?.CasterId, b?.CasterId, StringComparison.Ordinal)
                && a?.Rank == b?.Rank && a?.IsPermanent == b?.IsPermanent).All(x => x);
        }

        private static bool SameVector(WOTRMultiplayer.Entities.NetworkVector3 left,
            WOTRMultiplayer.Entities.NetworkVector3 right) => left == null && right == null
            || left != null && right != null
            && SameFloat(left.X, right.X) && SameFloat(left.Y, right.Y) && SameFloat(left.Z, right.Z);

        private static bool SameFloat(float left, float right)
        {
            if (float.IsNaN(left) || float.IsInfinity(left) || float.IsNaN(right) || float.IsInfinity(right))
                return false;
            try
            {
                return checked((long)Math.Round(left * 1000d, MidpointRounding.AwayFromZero))
                    == checked((long)Math.Round(right * 1000d, MidpointRounding.AwayFromZero));
            }
            catch (OverflowException) { return false; }
        }
    }
}
