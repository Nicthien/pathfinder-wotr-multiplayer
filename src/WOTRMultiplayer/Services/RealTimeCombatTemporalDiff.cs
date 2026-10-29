using System;
using System.Collections.Generic;
using System.Linq;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services
{
    /// <summary>
    /// Checks moving timer fields separately from the canonical hash. A small tolerance
    /// prevents normal network transit from looking like a rules divergence.
    /// </summary>
    public static class RealTimeCombatTemporalDiff
    {
        private static readonly long ToleranceTicks = TimeSpan.FromSeconds(1).Ticks;

        public static string Summarize(NetworkCombatState expected, NetworkCombatState actual)
        {
            if (expected == null || actual == null) return "combat-state-missing";

            var differences = new List<string>();
            var actualUnits = (actual.Units ?? []).Where(unit => unit?.Id != null)
                .GroupBy(unit => unit.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var expectedUnit in expected.Units ?? [])
            {
                if (expectedUnit?.Id == null || !actualUnits.TryGetValue(expectedUnit.Id, out var actualUnit))
                    continue; // Structural hash/preflight handles missing entities.
                var actualBuffs = (actualUnit.BuffCollection?.Buffs ?? [])
                    .Where(buff => buff?.Id != null)
                    .GroupBy(buff => buff.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                foreach (var expectedBuff in expectedUnit.BuffCollection?.Buffs ?? [])
                {
                    if (expectedBuff?.Id == null || !actualBuffs.TryGetValue(expectedBuff.Id, out var actualBuff))
                        continue;
                    if (!SameTime(expectedBuff.TimeLeft, actualBuff.TimeLeft))
                        differences.Add($"buff-time:{expectedUnit.Id}:{expectedBuff.Id}");
                    if (!SameTime(expectedBuff.NextTickTime, actualBuff.NextTickTime))
                        differences.Add($"buff-next-tick:{expectedUnit.Id}:{expectedBuff.Id}");
                    if (!SameTime(expectedBuff.NextResourceSpendingTime, actualBuff.NextResourceSpendingTime))
                        differences.Add($"buff-resource-time:{expectedUnit.Id}:{expectedBuff.Id}");
                }

                var expectedLevels = (expectedUnit.BuffCollection?.NegativeLevels ?? [])
                    .Where(level => level != null)
                    .GroupBy(level => (level.Count, level.SavingThrowType, level.EnergyDrainType));
                var actualLevels = (actualUnit.BuffCollection?.NegativeLevels ?? [])
                    .Where(level => level != null)
                    .GroupBy(level => (level.Count, level.SavingThrowType, level.EnergyDrainType))
                    .ToDictionary(group => group.Key, group => group.ToList());
                foreach (var group in expectedLevels)
                {
                    if (!actualLevels.TryGetValue(group.Key, out var localLevels)
                        || localLevels.Count != group.Count())
                        continue; // The structural hash handles absent or extra levels.
                    var hostDurations = group.Select(level => level.Duration)
                        .OrderBy(duration => duration).ToList();
                    var localDurations = localLevels.Select(level => level.Duration)
                        .OrderBy(duration => duration).ToList();
                    for (var index = 0; index < hostDurations.Count; index++)
                    {
                        if (!SameOptionalTime(hostDurations[index], localDurations[index]))
                            differences.Add($"negative-level-time:{expectedUnit.Id}:{group.Key.EnergyDrainType}:{group.Key.Count}");
                    }
                }
            }

            var actualAreas = (actual.AreaEffects ?? []).Where(area => area?.Id != null)
                .GroupBy(area => area.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var expectedArea in expected.AreaEffects ?? [])
            {
                if (expectedArea?.Id == null || !actualAreas.TryGetValue(expectedArea.Id, out var actualArea))
                    continue;
                if (!SameTicks(expectedArea.CreationGameTimeTicks, actualArea.CreationGameTimeTicks))
                    differences.Add($"area-creation-time:{expectedArea.Id}");
                if (expectedArea.DurationTicks.HasValue != actualArea.DurationTicks.HasValue
                    || expectedArea.DurationTicks.HasValue && !SameTicks(expectedArea.DurationTicks.Value,
                        actualArea.DurationTicks.Value))
                    differences.Add($"area-duration:{expectedArea.Id}");
            }

            if (differences.Count == 0) return null;
            var visible = differences.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)
                .Take(12).ToList();
            if (differences.Count > visible.Count) visible.Add($"and-{differences.Count - visible.Count}-more");
            return string.Join(",", visible);
        }

        private static bool SameTime(TimeSpan expected, TimeSpan actual) =>
            expected == TimeSpan.MaxValue || actual == TimeSpan.MaxValue
                ? expected == actual
                : SameTicks(expected.Ticks, actual.Ticks);

        private static bool SameOptionalTime(TimeSpan? expected, TimeSpan? actual) =>
            expected.HasValue == actual.HasValue
            && (!expected.HasValue || SameTime(expected.Value, actual.Value));

        private static bool SameTicks(long expected, long actual) =>
            Math.Abs((decimal)expected - actual) <= ToleranceTicks;
    }
}
