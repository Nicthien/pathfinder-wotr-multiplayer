using System;
using System.Collections.Generic;
using System.Linq;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services
{
    // A blueprint is not an instance identity: stacked effects may have
    // different casters, durations and ranks. Never match by blueprint alone.
    public static class RealTimeBuffState
    {
        public static string ValidateCollection(NetworkUnitBuffCollection expected, NetworkUnitBuffCollection local)
        {
            var error = Validate(expected?.Buffs, local?.Buffs);
            if (error != null) return error;
            if (expected.NegativeLevels == null || local.NegativeLevels == null
                || expected.NegativeLevels.Any(level => level == null)
                || local.NegativeLevels.Any(level => level == null)) return "invalid-negative-levels";
            var hostLevels = expected.NegativeLevels.GroupBy(level =>
                (level.Count, level.EnergyDrainType, level.SavingThrowType, level.Duration.HasValue))
                .ToDictionary(group => group.Key, group => group.Count());
            var peerLevels = local.NegativeLevels.GroupBy(level =>
                (level.Count, level.EnergyDrainType, level.SavingThrowType, level.Duration.HasValue))
                .ToDictionary(group => group.Key, group => group.Count());
            return hostLevels.Count != peerLevels.Count || hostLevels.Any(group =>
                !peerLevels.TryGetValue(group.Key, out var count) || group.Value != count)
                ? "negative-level-structure-mismatch" : null;
        }

        public static string Validate(IEnumerable<NetworkBuff> expected, IEnumerable<NetworkBuff> local)
        {
            if (expected == null || local == null) return "missing-buff-collection";
            var host = expected.ToList();
            var peer = local.ToList();
            foreach (var list in new[] { host, peer })
            {
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var buff in list)
                {
                    if (buff == null || string.IsNullOrWhiteSpace(buff.Id)
                        || string.IsNullOrWhiteSpace(buff.BlueprintId)) return "invalid-buff-identity";
                    if (!ids.Add(buff.Id)) return "duplicate-buff:" + buff.Id;
                    // Relative next-event times may be negative for an overdue
                    // event captured before its host callback finishes.
                    if (buff.Rank < 0 || buff.TimeLeft < TimeSpan.Zero)
                        return "invalid-buff-values:" + buff.Id;
                    var contextError = RealTimeBuffContextChain.Validate(buff.ContextChain);
                    if (contextError != null) return contextError + ":" + buff.Id;
                }
            }
            var byId = peer.ToDictionary(buff => buff.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var buff in host)
            {
                if (!byId.TryGetValue(buff.Id, out var match)) return "missing-buff:" + buff.Id;
                if (!string.Equals(buff.BlueprintId, match.BlueprintId, StringComparison.OrdinalIgnoreCase))
                    return "buff-blueprint-mismatch:" + buff.Id;
                if (!string.Equals(buff.CasterId, match.CasterId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(buff.SourceAbilityCasterId, match.SourceAbilityCasterId, StringComparison.OrdinalIgnoreCase))
                    return "buff-caster-mismatch:" + buff.Id;
            }
            var hostIds = new HashSet<string>(host.Select(buff => buff.Id), StringComparer.OrdinalIgnoreCase);
            var extra = peer.FirstOrDefault(buff => !hostIds.Contains(buff.Id));
            return extra == null ? null : "extra-buff:" + extra.Id;
        }
    }
}
