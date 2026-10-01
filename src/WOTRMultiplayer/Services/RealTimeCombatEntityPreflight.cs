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

            var missing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var unit in checkpoint.Units ?? [])
            {
                if (unit?.Id == null || !hasUnit(unit.Id))
                {
                    missing.Add($"missing-unit:{unit?.Id ?? "<null>"}");
                }
                if (unit?.Id == null) { continue; }
                foreach (var engagement in (unit.CombatState?.EngagedUnits ?? [])
                    .Concat(unit.CombatState?.EngagedBy ?? []))
                {
                    if (engagement?.UnitId == null || !hasUnit(engagement.UnitId))
                    {
                        missing.Add($"missing-engagement-target:{unit.Id}:{engagement?.UnitId ?? "<null>"}");
                    }
                }
            }
            foreach (var area in checkpoint.AreaEffects ?? [])
            {
                if (area?.Id == null || !hasArea(area))
                {
                    missing.Add($"missing-area:{area?.Id ?? "<null>"}");
                }
                if (area?.Id == null) { continue; }
                foreach (var unitId in area.UnitsInside ?? [])
                {
                    if (unitId == null || !hasUnit(unitId))
                    {
                        missing.Add($"missing-area-unit:{area.Id}:{unitId ?? "<null>"}");
                    }
                }
            }
            if (missing.Count == 0)
            {
                return null;
            }
            var visible = missing.OrderBy(x => x, StringComparer.Ordinal).Take(12).ToList();
            if (missing.Count > visible.Count)
            {
                visible.Add($"and-{missing.Count - visible.Count}-more");
            }
            return string.Join(",", visible);
        }
    }
}
