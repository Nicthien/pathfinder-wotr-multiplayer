using System;
using System.Collections.Generic;
using System.Linq;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;

namespace WOTRMultiplayer.Services
{
    /// <summary>Rejects a checkpoint before any partial mutation when required game entities are absent.</summary>
    public static class RealTimeCombatEntityPreflight
    {
        public static string FindMissing(NetworkCombatState checkpoint, Func<string, bool> hasUnit,
            Func<NetworkAreaEffect, bool> hasArea)
        {
            if (checkpoint == null || hasUnit == null || hasArea == null)
            {
                throw new ArgumentNullException(checkpoint == null ? nameof(checkpoint)
                    : hasUnit == null ? nameof(hasUnit) : nameof(hasArea));
            }

            var missing = new List<string>();
            foreach (var unit in checkpoint.Units ?? [])
            {
                if (unit?.Id == null || !hasUnit(unit.Id))
                {
                    missing.Add($"missing-unit:{unit?.Id ?? "<null>"}");
                }
            }
            foreach (var area in checkpoint.AreaEffects ?? [])
            {
                if (area?.Id == null || !hasArea(area))
                {
                    missing.Add($"missing-area:{area?.Id ?? "<null>"}");
                }
            }
            if (missing.Count == 0)
            {
                return null;
            }
            missing.Sort(StringComparer.Ordinal);
            var visible = missing.Take(12).ToList();
            if (missing.Count > visible.Count)
            {
                visible.Add($"and-{missing.Count - visible.Count}-more");
            }
            return string.Join(",", visible);
        }
    }
}
