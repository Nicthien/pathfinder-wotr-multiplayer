using System;
using System.Collections.Generic;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services
{
    /// <summary>Reject a turn-end checkpoint before any unit has been mutated.</summary>
    public static class TurnEndUnitUpdatePreflight
    {
        public static string FindFailure(List<NetworkUnit> units, Func<string, bool> hasUnit,
            bool updatePosition, Func<string, string> getLocalBlueprintId = null,
            Func<string, string> getLocalSceneName = null)
        {
            if (units == null || hasUnit == null) return "missing-checkpoint";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in units)
            {
                if (string.IsNullOrWhiteSpace(unit?.Id)) return "missing-unit-id";
                if (!seen.Add(unit.Id)) return $"duplicate-unit:{unit.Id}";
                if (!hasUnit(unit.Id)) return $"missing-unit:{unit.Id}";
                if (getLocalBlueprintId != null && (string.IsNullOrWhiteSpace(unit.BlueprintId)
                    || !string.Equals(unit.BlueprintId, getLocalBlueprintId(unit.Id),
                        StringComparison.OrdinalIgnoreCase)))
                    return $"wrong-blueprint:{unit.Id}";
                if (getLocalSceneName != null && !string.Equals(unit.SceneName,
                    getLocalSceneName(unit.Id), StringComparison.Ordinal))
                    return $"wrong-scene:{unit.Id}";
                if (unit.Descriptor?.State?.Prone == null) return $"invalid-state:{unit.Id}";
                if (updatePosition && (unit.Position == null
                    || float.IsNaN(unit.Position.X) || float.IsInfinity(unit.Position.X)
                    || float.IsNaN(unit.Position.Y) || float.IsInfinity(unit.Position.Y)
                    || float.IsNaN(unit.Position.Z) || float.IsInfinity(unit.Position.Z)
                    || float.IsNaN(unit.Orientation) || float.IsInfinity(unit.Orientation)))
                    return $"invalid-position:{unit.Id}";
            }
            return null;
        }
    }
}
