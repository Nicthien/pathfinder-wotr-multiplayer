using System;
using UnityEngine;
using WOTRMultiplayer.Abstractions;

namespace WOTRMultiplayer.Services
{
    /// <summary>Publishes a millimetre-precision three-dimensional combat destination from the host.</summary>
    public static class RealTimeVector3Authority
    {
        private const float Precision = 1000f;

        public static Vector3 Resolve(IMultiplayer multiplayer, string key, Vector3 local)
        {
            if (multiplayer == null) throw new ArgumentNullException(nameof(multiplayer));
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("Destination key is required", nameof(key));
            if (!multiplayer.IsRealTimeCombatActive || multiplayer.IsRealTimeCombatBarrierActive)
                return local;

            var localX = Quantize(local.x);
            var localY = Quantize(local.y);
            var localZ = Quantize(local.z);
            var hostX = multiplayer.ResolveRealTimeDiceRoll($"{key}:x", () => localX);
            var hostY = multiplayer.ResolveRealTimeDiceRoll($"{key}:y", () => localY);
            var hostZ = multiplayer.ResolveRealTimeDiceRoll($"{key}:z", () => localZ);
            return new Vector3(hostX / Precision, hostY / Precision, hostZ / Precision);
        }

        private static int Quantize(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidOperationException("A combat destination cannot contain a non-finite coordinate.");
            return checked((int)Math.Round(value * Precision, MidpointRounding.AwayFromZero));
        }
    }
}
