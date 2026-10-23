using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace WOTRMultiplayer.Services.GameInteraction
{
    internal static class CombatRegroupPlanner
    {
        internal readonly struct Point
        {
            internal Point(string unitId, float x, float z)
            {
                UnitId = unitId;
                X = x;
                Z = z;
            }

            internal string UnitId { get; }

            internal float X { get; }

            internal float Z { get; }
        }

        internal static string FindAnchor(
            IReadOnlyCollection<Point> candidates,
            IReadOnlyCollection<Point> hostiles)
        {
            if (candidates == null || hostiles == null || candidates.Count == 0 || hostiles.Count == 0)
            {
                return null;
            }

            return candidates
                .OrderBy(candidate => hostiles.Min(hostile => SqrDistanceXZ(candidate, hostile)))
                .ThenBy(candidate => candidate.UnitId, System.StringComparer.OrdinalIgnoreCase)
                .Select(candidate => candidate.UnitId)
                .FirstOrDefault();
        }

        internal static bool IsFartherThan(Vector3 position, Vector3 anchor, float minimumDistance)
        {
            return SqrDistanceXZ(position, anchor) > minimumDistance * minimumDistance;
        }

        private static float SqrDistanceXZ(Vector3 left, Vector3 right)
        {
            var x = left.x - right.x;
            var z = left.z - right.z;
            return x * x + z * z;
        }

        private static float SqrDistanceXZ(Point left, Point right)
        {
            var x = left.X - right.X;
            var z = left.Z - right.Z;
            return x * x + z * z;
        }
    }
}
