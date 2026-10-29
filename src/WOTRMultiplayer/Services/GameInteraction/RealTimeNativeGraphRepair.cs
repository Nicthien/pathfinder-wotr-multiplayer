using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Native unit parts may retain references to a retired area even after it
    // leaves all entity pools. Keep this obligation outside those filtered pools.
    public static class RealTimeNativeGraphRepair
    {
        private static readonly ConditionalWeakTable<object, HashSet<object>> Pending = new();
        public static bool RequiresRebuild(object world) => world != null
            && Pending.TryGetValue(world, out var obligations) && obligations.Count != 0;
        public static void RequireMechanicalState(object world)
        {
            if (RequiresRebuild(world)) throw new InvalidOperationException("native-area-graph-requires-mechanical-reconstruction");
        }
        internal static void Add(object world, object owner) => Pending.GetValue(world, _ => new()).Add(owner);
        internal static bool Contains(object world, object owner) => world != null && owner != null
            && Pending.TryGetValue(world, out var obligations) && obligations.Contains(owner);
        internal static void Remove(object world, object owner)
        {
            if (Pending.TryGetValue(world, out var obligations)) obligations.Remove(owner);
        }

        internal static bool IsOnlyObligation(object world, object owner) => world != null && owner != null
            && Pending.TryGetValue(world, out var obligations) && obligations.Count == 1 && obligations.Contains(owner);
    }
}
