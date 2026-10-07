using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;

namespace WOTRMultiplayer.Services.GameInteraction
{
    public static class RealTimeAreaDiscovery
    {
        // Include owned replicas hidden by native culling. Never revive ordinary
        // inactive zones or return a second entity for the same wire identity.
        public static List<AreaEffectEntityData> Collect(IEnumerable<AreaEffectEntityData> active,
            IEnumerable<AreaEffectEntityData> all)
        {
            var result = new List<AreaEffectEntityData>();
            foreach (var area in active.Concat(all.Where(RealTimeAreaReplicaFactory.IsReplica)))
                if (!result.Any(previous => ReferenceEquals(previous, area))) result.Add(area);
            if (result.Any(area => area == null || string.IsNullOrWhiteSpace(area.UniqueId))
                || result.GroupBy(area => area.UniqueId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new InvalidOperationException("ambiguous-area-replica-discovery");
            return result;
        }
    }
}
