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
            if (checkpoint.Units == null) missing.Add("invalid-combat:units");
            if (checkpoint.AreaEffects == null) missing.Add("invalid-combat:areas");
            foreach (var unit in checkpoint.Units ?? [])
            {
                var exists = unit?.Id != null && hasUnit(unit.Id);
                if (!exists)
                {
                    missing.Add($"missing-unit:{unit?.Id ?? "<null>"}");
                }
                else
                {
                    if (unit.Position == null) missing.Add($"invalid-unit:{unit.Id}:position");
                    if (unit.Descriptor?.State?.Prone == null) missing.Add($"invalid-unit:{unit.Id}:state");
                    if (unit.Descriptor?.Stats == null) missing.Add($"invalid-unit:{unit.Id}:stats");
                    if (unit.BuffCollection?.Buffs == null || unit.BuffCollection.NegativeLevels == null)
                        missing.Add($"invalid-unit:{unit.Id}:buffs");
                    else if (unit.BuffCollection.Buffs.Any(buff => buff == null) ||
                             unit.BuffCollection.NegativeLevels.Any(level => level == null))
                        missing.Add($"invalid-unit:{unit.Id}:buff-entry");
                    if (unit.CombatState != null &&
                        (unit.CombatState.EngagedUnits == null || unit.CombatState.EngagedBy == null ||
                         unit.CombatState.EngagedUnits.Any(engagement => engagement == null) ||
                         unit.CombatState.EngagedBy.Any(engagement => engagement == null)))
                        missing.Add($"invalid-unit:{unit.Id}:engagements");
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
                var exists = area?.Id != null && hasArea(area);
                if (!exists)
                {
                    missing.Add($"missing-area:{area?.Id ?? "<null>"}");
                }
                else
                {
                    if (area.Position == null) missing.Add($"invalid-area:{area.Id}:position");
                    if (area.UnitsInside == null) missing.Add($"invalid-area:{area.Id}:occupants");
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
