using System;
using System.Collections.Generic;
using System.Linq;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services
{
    public static class RealTimeBuffIdentityPlan
    {
        // TB matches effects by blueprint, whereas RT snapshots address instances.
        // Only bridge an unambiguous existing native effect; never merge stacks.
        public static Dictionary<string, string> Create(IEnumerable<NetworkBuff> host, IEnumerable<NetworkBuff> client)
        {
            var expected = host.ToList(); var local = client.ToList();
            var ids = new HashSet<string>(expected.Select(b => b.Id), StringComparer.OrdinalIgnoreCase);
            var localIds = new HashSet<string>(local.Select(b => b.Id), StringComparer.OrdinalIgnoreCase);
            var missing = expected.Where(b => !localIds.Contains(b.Id)).ToList();
            var extra = local.Where(b => !ids.Contains(b.Id)).ToList();
            var plan = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in missing)
            {
                var matches = extra.Where(b => SameEffect(target, b)).ToList();
                if (matches.Count != 1 || missing.Count(b => SameEffect(b, matches[0])) != 1) continue;
                plan.Add(matches[0].Id, target.Id);
            }
            return plan;
        }
        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static bool SameEffect(NetworkBuff a, NetworkBuff b) => Same(a.BlueprintId, b.BlueprintId)
            && Same(a.CasterId, b.CasterId) && Same(a.SourceAbilityCasterId, b.SourceAbilityCasterId)
            && Same(a.SourceAbility?.BlueprintId, b.SourceAbility?.BlueprintId)
            && a.IsPermanent == b.IsPermanent && a.IsHidden == b.IsHidden && a.Rank == b.Rank;
    }
}
