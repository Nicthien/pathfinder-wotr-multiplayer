using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services
{
    public static class RealTimeBuffContextChain
    {
        public const int MaximumDepth = 16;
        public const int MaximumValues = 256;
        private sealed class IdentityComparer<T> : IEqualityComparer<T> where T : class
        {
            public bool Equals(T left, T right) => ReferenceEquals(left, right);
            public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
        }

        // Child first, then each parent. Flat encoding prevents recursive wire
        // objects and permits cycle/depth checks before collecting any values.
        public static List<NetworkBuffContextState> Capture<T>(T root, Func<T, T> parent,
            Func<T, NetworkBuffContextState> sample) where T : class
        {
            if (root == null || parent == null || sample == null) throw new ArgumentNullException();
            var seen = new HashSet<T>(new IdentityComparer<T>());
            var chain = new List<T>();
            for (var current = root; current != null; current = parent(current))
            {
                if (!seen.Add(current)) throw new InvalidOperationException("buff-context-cycle");
                if (chain.Count == MaximumDepth) throw new InvalidOperationException("buff-context-too-deep");
                chain.Add(current);
            }
            var values = chain.Select(sample).ToList();
            var error = Validate(values);
            if (error != null) throw new InvalidOperationException(error);
            return values;
        }

        public static string Validate(IReadOnlyList<NetworkBuffContextState> chain)
        {
            if (chain == null) return null; // Old/default checkpoints contain no presentation context.
            if (chain.Count == 0 || chain.Count > MaximumDepth) return "invalid-buff-context-depth";
            foreach (var node in chain)
            {
                if (node == null || string.IsNullOrWhiteSpace(node.BlueprintId) || node.Params == null
                    || node.Ranks == null || node.SharedValues == null || node.Properties == null
                    || node.Ranks.Count > MaximumValues || node.SharedValues.Count > MaximumValues
                    || node.Properties.Count > MaximumValues) return "invalid-buff-context-values";
                // AssociatedBlueprint and the ability are independent native
                // references (converted abilities can keep the original source).
                if (node.IsAbilityContext && (node.SourceAbility == null
                    || string.IsNullOrWhiteSpace(node.SourceAbility.BlueprintId)))
                    return "invalid-buff-context-ability";
                if (node.Direction == null || !Finite(node.Direction.X) || !Finite(node.Direction.Y) || !Finite(node.Direction.Z))
                    return "invalid-buff-context-direction";
                if (node.MainTarget != null && (node.MainTarget.Point == null
                    || !Finite(node.MainTarget.Point.X) || !Finite(node.MainTarget.Point.Y) || !Finite(node.MainTarget.Point.Z)
                    || node.MainTarget.Orientation.HasValue && !Finite(node.MainTarget.Orientation.Value)))
                    return "invalid-buff-context-target";
            }
            return null;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
