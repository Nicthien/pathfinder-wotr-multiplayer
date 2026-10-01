using System;
using System.Collections.Generic;
using System.Linq;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;

namespace WOTRMultiplayer.Services
{
    /// <summary>Rejects a checkpoint before any partial mutation when required game entities are absent.</summary>
    public static class RealTimeCombatEntityPreflight
    {
        public static string FindMissing(NetworkCombatState checkpoint, Func<string, bool> hasUnit,
            Func<NetworkAreaEffect, bool> hasArea, Func<NetworkAreaEffect, bool> hasAreaView = null,
            Func<string, string> getUnitBlueprintId = null,
            Func<NetworkAreaEffect, string> getAreaBlueprintId = null,
            Func<string, string> getUnitSceneName = null,
            Func<NetworkAreaEffect, string> getAreaSceneName = null,
            IEnumerable<string> localCombatUnitIds = null,
            IEnumerable<string> localAreaIds = null)
        {
            if (checkpoint == null || hasUnit == null || hasArea == null)
            {
                throw new ArgumentNullException(checkpoint == null ? nameof(checkpoint)
                    : hasUnit == null ? nameof(hasUnit) : nameof(hasArea));
            }

            var missing = new HashSet<string>(StringComparer.Ordinal);
            if (checkpoint.Units == null) missing.Add("invalid-combat:units");
            if (checkpoint.AreaEffects == null) missing.Add("invalid-combat:areas");
            var unitIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var areaIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in checkpoint.Units ?? [])
            {
                if (unit?.Id != null && !unitIds.Add(unit.Id))
                    missing.Add($"duplicate-unit:{unit.Id}");
                var exists = unit?.Id != null && hasUnit(unit.Id);
                if (!exists)
                {
                    missing.Add($"missing-unit:{unit?.Id ?? "<null>"}");
                }
                else
                {
                    if (string.IsNullOrEmpty(unit.BlueprintId) ||
                        (getUnitBlueprintId != null && !string.Equals(unit.BlueprintId,
                            getUnitBlueprintId(unit.Id), StringComparison.OrdinalIgnoreCase)))
                        missing.Add($"invalid-unit:{unit.Id}:blueprint");
                    if (getUnitSceneName != null && !string.Equals(unit.SceneName,
                        getUnitSceneName(unit.Id), StringComparison.Ordinal))
                        missing.Add($"invalid-unit:{unit.Id}:scene");
                    if (!IsFinite(unit.Position)) missing.Add($"invalid-unit:{unit.Id}:position");
                    if (float.IsNaN(unit.Orientation) || float.IsInfinity(unit.Orientation))
                        missing.Add($"invalid-unit:{unit.Id}:orientation");
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
                if (area?.Id != null && !areaIds.Add(area.Id))
                    missing.Add($"duplicate-area:{area.Id}");
                var exists = area?.Id != null && hasArea(area);
                if (!exists)
                {
                    missing.Add($"missing-area:{area?.Id ?? "<null>"}");
                }
                else
                {
                    if (string.IsNullOrEmpty(area.BlueprintId) ||
                        (getAreaBlueprintId != null && !string.Equals(area.BlueprintId,
                            getAreaBlueprintId(area), StringComparison.OrdinalIgnoreCase)))
                        missing.Add($"invalid-area:{area.Id}:blueprint");
                    if (getAreaSceneName != null && !string.Equals(area.SceneName,
                        getAreaSceneName(area), StringComparison.Ordinal))
                        missing.Add($"invalid-area:{area.Id}:scene");
                    if (hasAreaView != null && !hasAreaView(area))
                        missing.Add($"invalid-area:{area.Id}:view");
                    if (!IsFinite(area.Position)) missing.Add($"invalid-area:{area.Id}:position");
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
            if (localCombatUnitIds != null && checkpoint.Units != null)
            {
                foreach (var id in localCombatUnitIds)
                {
                    if (id != null && !unitIds.Contains(id)) missing.Add($"extra-unit:{id}");
                }
            }
            if (localAreaIds != null && checkpoint.AreaEffects != null)
            {
                foreach (var id in localAreaIds)
                {
                    if (id != null && !areaIds.Contains(id)) missing.Add($"extra-area:{id}");
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

        private static bool IsFinite(NetworkVector3 value) => value != null
            && !float.IsNaN(value.X) && !float.IsInfinity(value.X)
            && !float.IsNaN(value.Y) && !float.IsInfinity(value.Y)
            && !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);
    }
}
