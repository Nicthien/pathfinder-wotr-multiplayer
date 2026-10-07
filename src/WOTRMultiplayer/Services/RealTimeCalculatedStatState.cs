using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Stats;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services
{
    public static class RealTimeCalculatedStatState
    {
        public static string ValidateValues(IReadOnlyList<NetworkCalculatedStat> stats)
        {
            if (stats == null) return null; // Absent from the unchanged turn-based path.
            if (stats.Count == 0 || stats.Count > 256) return "invalid-calculated-stat-count";
            var types = new HashSet<int>();
            foreach (var stat in stats)
            {
                if (stat?.Values == null || !Enum.IsDefined(typeof(StatType), stat.Type)) return "invalid-calculated-stat";
                if (!types.Add(stat.Type)) return "duplicate-calculated-stat:" + stat.Type;
                // Penalties can legitimately produce negative non-vital values.
            }
            return null;
        }

        public static string Validate(IReadOnlyList<NetworkCalculatedStat> expected, IReadOnlyList<NetworkCalculatedStat> actual)
        {
            var error = ValidateValues(expected) ?? ValidateValues(actual);
            if (error != null) return error;
            if ((expected == null) != (actual == null)) return "missing-calculated-stats";
            if (expected == null) return null;
            return expected.Count != actual.Count || !new HashSet<int>(expected.Select(stat => stat.Type))
                .SetEquals(actual.Select(stat => stat.Type)) ? "calculated-stat-shape-mismatch" : null;
        }

        public static string ValidateVitals(IReadOnlyList<NetworkCalculatedStat> stats, NetworkUnitVitals vitals)
        {
            var error = ValidateValues(stats);
            if (error != null || stats == null) return error;
            if (vitals == null) return "missing-calculated-stat-vitals";
            foreach (var expected in new[] { (StatType.HitPoints, vitals.HitPoints),
                (StatType.TemporaryHitPoints, vitals.TemporaryHitPoints), (StatType.DamageNonLethal, vitals.NonLethal) })
            {
                var value = stats.FirstOrDefault(stat => stat.Type == (int)expected.Item1)?.Values;
                if (!Same(value, expected.Item2)) return "calculated-stat-vitals-mismatch:" + expected.Item1;
            }
            return null;
        }

        private static bool Same(NetworkStatValues left, NetworkStatValues right) => left != null && right != null
            && left.BaseValue == right.BaseValue && left.ModifiedValue == right.ModifiedValue
            && left.ModifiedValueRaw == right.ModifiedValueRaw && left.PermanentValue == right.PermanentValue;
    }
}
