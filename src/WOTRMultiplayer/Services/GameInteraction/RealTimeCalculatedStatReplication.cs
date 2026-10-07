using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Stats;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services.GameInteraction
{
    public static class RealTimeCalculatedStatReplication
    {
        public static List<NetworkCalculatedStat> Capture(IEnumerable<ModifiableValue> stats)
        {
            if (stats == null) throw new InvalidOperationException("missing-native-calculated-stats");
            var result = stats.Select(stat => stat == null ? null : new NetworkCalculatedStat
                { Type = (int)stat.Type, Values = RealTimeUnitResultReplication.CaptureStat(stat) }).ToList();
            var error = RealTimeCalculatedStatState.ValidateValues(result);
            if (error != null) throw new InvalidOperationException(error);
            return result;
        }

        public static void Apply(IEnumerable<ModifiableValue> stats, IReadOnlyList<NetworkCalculatedStat> expected)
        {
            if (expected == null) return;
            var targets = stats?.ToList() ?? throw new InvalidOperationException("missing-native-calculated-stats");
            var error = RealTimeCalculatedStatState.Validate(expected, Capture(targets));
            if (error != null) throw new InvalidOperationException(error);
            var byType = targets.ToDictionary(stat => (int)stat.Type);
            foreach (var stat in expected) RealTimeUnitResultReplication.ApplyStat(byType[stat.Type], stat.Values);
        }
    }
}
