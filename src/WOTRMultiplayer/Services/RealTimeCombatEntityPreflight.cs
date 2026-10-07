using System;
using System.Collections.Generic;
using System.Linq;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Units;

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
            IEnumerable<string> localAreaIds = null,
            Func<string, IEnumerable<NetworkBuff>> getLocalBuffs = null,
            Func<NetworkBuff, bool> canRecreateBuff = null,
            Func<NetworkAreaEffect, NetworkAreaEffect> getLocalArea = null,
            Func<NetworkAreaEffect, bool> canRecreateArea = null,
            Func<string, bool> canRemoveArea = null)
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
                    else if (getLocalBuffs != null && canRecreateBuff != null)
                    {
                        var localBuffs = (getLocalBuffs(unit.Id) ?? []).ToList();
                        foreach (var buff in unit.BuffCollection.Buffs)
                        {
                            if (localBuffs.Any(local => local != null &&
                                    (string.Equals(local.Id, buff.Id, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(local.BlueprintId, buff.BlueprintId, StringComparison.OrdinalIgnoreCase)))
                                || canRecreateBuff(buff)) continue;
                            missing.Add($"unreconstructable-buff:{unit.Id}:{buff.Id ?? buff.BlueprintId ?? "<null>"}");
                        }
                    }
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
                if (!exists && (area?.Id == null || canRecreateArea == null || !canRecreateArea(area)))
                {
                    missing.Add($"missing-area:{area?.Id ?? "<null>"}");
                }
                else
                {
                    if (string.IsNullOrEmpty(area.BlueprintId) ||
                        (exists && getAreaBlueprintId != null && !string.Equals(area.BlueprintId,
                            getAreaBlueprintId(area), StringComparison.OrdinalIgnoreCase)))
                        missing.Add($"invalid-area:{area.Id}:blueprint");
                    if (exists && getAreaSceneName != null && !string.Equals(area.SceneName,
                        getAreaSceneName(area), StringComparison.Ordinal))
                        missing.Add($"invalid-area:{area.Id}:scene");
                    if (exists && hasAreaView != null && !hasAreaView(area))
                        missing.Add($"invalid-area:{area.Id}:view");
                    if (!IsFinite(area.Position)) missing.Add($"invalid-area:{area.Id}:position");
                    if (float.IsNaN(area.Orientation) || float.IsInfinity(area.Orientation))
                        missing.Add($"invalid-area:{area.Id}:orientation");
                    var contextError = RealTimeBuffContextChain.Validate(area.ContextChain);
                    if (contextError != null) missing.Add($"invalid-area:{area.Id}:{contextError}");
                    if (!exists && (area.ContextChain == null || area.ContextChain.Count == 0))
                        missing.Add($"invalid-area:{area.Id}:reconstruction-context");
                    if (area.TargetPoint != null && !IsFinite(area.TargetPoint))
                        missing.Add($"invalid-area:{area.Id}:target-point");
                    if (area.TargetOrientation.HasValue &&
                        (float.IsNaN(area.TargetOrientation.Value) || float.IsInfinity(area.TargetOrientation.Value)))
                        missing.Add($"invalid-area:{area.Id}:target-orientation");
                    if (!exists && (string.IsNullOrWhiteSpace(area.Id) || string.IsNullOrWhiteSpace(area.SceneName)
                        || (area.TargetPoint == null) == (area.TargetUnitId == null)
                        || area.IsAttachedToUnit && area.TargetUnitId == null))
                        missing.Add($"invalid-area:{area.Id}:reconstruction-target-or-scene");
                    if (exists && getLocalArea != null)
                    {
                        var local = getLocalArea(area);
                        if (local == null || !string.Equals(area.CasterUnitId, local.CasterUnitId, StringComparison.Ordinal)
                            || !string.Equals(area.OwnerUnitId, local.OwnerUnitId, StringComparison.Ordinal)
                            || !string.Equals(area.SourceAbilityBlueprintId, local.SourceAbilityBlueprintId,
                                StringComparison.OrdinalIgnoreCase))
                            missing.Add($"invalid-area:{area.Id}:context");
                        if (local == null || !string.Equals(area.TargetUnitId, local.TargetUnitId, StringComparison.Ordinal)
                            || !SameTargetPoint(area.TargetPoint, local.TargetPoint)
                            || !SameTargetOrientation(area.TargetOrientation, local.TargetOrientation)
                            || area.IsAttachedToUnit != local.IsAttachedToUnit)
                            missing.Add($"invalid-area:{area.Id}:target");
                    }
                    if (area.UnitsInside == null) missing.Add($"invalid-area:{area.Id}:occupants");
                    else if (area.UnitsInside.Any(string.IsNullOrWhiteSpace)
                        || area.UnitsInside.Distinct(StringComparer.OrdinalIgnoreCase).Count() != area.UnitsInside.Count)
                        missing.Add($"invalid-area:{area.Id}:occupants");
                }
                if (area?.Id == null) { continue; }
                if (area.TargetUnitId != null && !hasUnit(area.TargetUnitId))
                    missing.Add($"missing-area-target:{area.Id}:{area.TargetUnitId}");
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
                    if (id != null && !areaIds.Contains(id) && (canRemoveArea == null || !canRemoveArea(id)))
                        missing.Add($"extra-area:{id}");
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

        private static bool SameTargetPoint(NetworkVector3 first, NetworkVector3 second) =>
            first == null ? second == null : second != null && IsFinite(first) && IsFinite(second)
            && Quantize(first.X) == Quantize(second.X)
            && Quantize(first.Y) == Quantize(second.Y)
            && Quantize(first.Z) == Quantize(second.Z);

        private static long Quantize(float value) => checked((long)Math.Round(value * 1000d,
            MidpointRounding.AwayFromZero));

        private static bool SameTargetOrientation(float? first, float? second) =>
            first.HasValue ? second.HasValue && !float.IsNaN(first.Value) && !float.IsInfinity(first.Value)
                && !float.IsNaN(second.Value) && !float.IsInfinity(second.Value)
                && Quantize(first.Value) == Quantize(second.Value) : !second.HasValue;
    }
}
