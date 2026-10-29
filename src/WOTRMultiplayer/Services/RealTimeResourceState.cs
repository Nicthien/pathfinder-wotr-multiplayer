using System;
using System.Collections.Generic;
using System.Linq;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services
{
    public static class RealTimeResourceState
    {
        // Validate the whole resource set before updating any amount. Missing
        // resources require entity reconstruction, never replaying Add/Spend.
        public static string Validate(IEnumerable<NetworkAbilityResource> expected,
            IEnumerable<NetworkAbilityResource> actual)
        {
            var host = Index(expected);
            var client = Index(actual);
            if (host == null || client == null) return "invalid-resources";
            foreach (var id in host.Keys.Union(client.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!host.ContainsKey(id)) return "extra-resource:" + id;
                if (!client.ContainsKey(id)) return "missing-resource:" + id;
                if (host[id].RetainCount != client[id].RetainCount) return "resource-retain-count:" + id;
            }
            return null;
        }

        private static Dictionary<string, NetworkAbilityResource> Index(IEnumerable<NetworkAbilityResource> resources)
        {
            if (resources == null) return null;
            var indexed = new Dictionary<string, NetworkAbilityResource>(StringComparer.OrdinalIgnoreCase);
            foreach (var resource in resources)
            {
                if (resource == null || string.IsNullOrWhiteSpace(resource.BlueprintId)
                    || resource.Amount < 0 || resource.RetainCount < 1
                    || indexed.ContainsKey(resource.BlueprintId)) return null;
                indexed.Add(resource.BlueprintId, resource);
            }
            return indexed;
        }
    }
}
