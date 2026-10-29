using System;
using UnityEngine;
using WOTRMultiplayer.Abstractions;

namespace WOTRMultiplayer.Services
{
    /// <summary>Publishes a millimetre-precision two-dimensional combat outcome through the host roll channel.</summary>
    public static class RealTimeVector2Authority
    {
        private const float Precision = 1000f;

        public static Vector2 Resolve(IMultiplayer multiplayer, string key, Vector2 local)
        {
            if (multiplayer == null) throw new ArgumentNullException(nameof(multiplayer));
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("Outcome key is required", nameof(key));
            if (!multiplayer.IsRealTimeCombatActive || multiplayer.IsRealTimeCombatBarrierActive)
                return local;

            var localX = Quantize(local.x);
            var localY = Quantize(local.y);
            var hostX = multiplayer.ResolveRealTimeDiceRoll($"{key}:x", () => localX);
            var hostY = multiplayer.ResolveRealTimeDiceRoll($"{key}:y", () => localY);
            return new Vector2(hostX / Precision, hostY / Precision);
        }

        private static int Quantize(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidOperationException("A projectile outcome cannot contain a non-finite coordinate.");
            return checked((int)Math.Round(value * Precision, MidpointRounding.AwayFromZero));
        }
    }
}
