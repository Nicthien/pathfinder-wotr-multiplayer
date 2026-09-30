using System;
using System.Collections.Generic;
using System.Linq;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;

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
            var expectedUnits = expected.Units?.Where(x => x?.Id != null).ToDictionary(x => x.Id) ?? [];
            var actualUnits = actual.Units?.Where(x => x?.Id != null).ToDictionary(x => x.Id) ?? [];
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
                else if (Hash(new NetworkCombatState { Units = [hostUnit] })
                    != Hash(new NetworkCombatState { Units = [clientUnit] }))
                {
                    differences.Add($"unit:{id}");
                }
            }

            var expectedAreas = expected.AreaEffects?.Where(x => x?.Id != null).ToDictionary(x => x.Id) ?? [];
            var actualAreas = actual.AreaEffects?.Where(x => x?.Id != null).ToDictionary(x => x.Id) ?? [];
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
                else if (Hash(new NetworkCombatState { AreaEffects = [hostArea] })
                    != Hash(new NetworkCombatState { AreaEffects = [clientArea] }))
                {
                    differences.Add($"area:{id}");
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
    }
}
