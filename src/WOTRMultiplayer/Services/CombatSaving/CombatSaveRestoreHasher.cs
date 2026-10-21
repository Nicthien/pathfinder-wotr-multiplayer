using System;
using System.Linq;
using Newtonsoft.Json;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;

namespace WOTRMultiplayer.Services.CombatSaving
{
    /// <summary>
    /// Fingerprint of the mechanics that Owlcat's native save format preserves.
    /// The ordinary real-time hash remains stricter and is still used while the
    /// two live peers are frozen before publication.
    /// </summary>
    internal static class CombatSaveRestoreHasher
    {
        internal static ulong Compute(NetworkCombatState state)
        {
            var projected = Project(state);
            return RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = projected });
        }

        internal static NetworkCombatState Project(NetworkCombatState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var projected = JsonConvert.DeserializeObject<NetworkCombatState>(
                JsonConvert.SerializeObject(state)) ?? throw new InvalidOperationException("combat-save-restore-projection-failed");

            var areas = projected.AreaEffects ?? [];
            foreach (var unit in projected.Units ?? [])
            {
                if (unit == null) continue;
                TruncateNativePosition(unit.Position);
                if (unit.BuffCollection?.Buffs == null) continue;

                unit.BuffCollection.Buffs = unit.BuffCollection.Buffs
                    .Where(buff => !IsAreaDerived(buff, areas))
                    .Select(buff =>
                    {
                        if (buff == null) return null;
                        // Fact ids and calculated presentation contexts are rebuilt
                        // by the native loader. Blueprint/caster/rank/permanence and
                        // source ability below remain part of the fingerprint.
                        buff.Id = null;
                        buff.ContextChain = null;
                        return buff;
                    })
                    .OrderBy(StableJson, StringComparer.Ordinal)
                    .ToList();
                for (var i = 0; i < unit.BuffCollection.Buffs.Count; i++)
                    if (unit.BuffCollection.Buffs[i] != null)
                        unit.BuffCollection.Buffs[i].Id = "buff-" + i.ToString("D8");
            }

            projected.AreaEffects = areas.Select(area =>
                {
                    if (area == null) return null;
                    area.Id = null;
                    TruncateNativePosition(area.Position);
                    TruncateNativePosition(area.TargetPoint);
                    // Permanent/attached areas are reconstructed at the current
                    // game time by Owlcat's native loader. Their creation stamp
                    // is therefore an allocation detail, not restored mechanics.
                    // Timed areas keep their stamp and duration protected.
                    if (area.DurationTicks == null) area.CreationGameTimeTicks = 0;
                    // Occupants and the resulting aura facts are derived again by
                    // AreaEffectEntityData after a normal native load.
                    area.UnitsInside?.Clear();
                    area.ContextChain = null;
                    return area;
                })
                .OrderBy(StableJson, StringComparer.Ordinal)
                .ToList();
            for (var i = 0; i < projected.AreaEffects.Count; i++)
                if (projected.AreaEffects[i] != null)
                    projected.AreaEffects[i].Id = "area-" + i.ToString("D8");

            foreach (var projectile in projected.Projectiles ?? [])
                TruncateNativePosition(projectile?.Position);

            return projected;
        }

        private static bool IsAreaDerived(Entities.Units.NetworkBuff buff,
            System.Collections.Generic.IEnumerable<Entities.AreaEffects.NetworkAreaEffect> areas)
        {
            if (buff?.ContextChain == null || string.IsNullOrWhiteSpace(buff.CasterId)) return false;
            return areas.Any(area => area != null
                && string.Equals(area.CasterUnitId, buff.CasterId, StringComparison.Ordinal)
                && buff.ContextChain.Any(context => context != null
                    && string.Equals(context.BlueprintId, area.BlueprintId, StringComparison.OrdinalIgnoreCase)));
        }

        private static string StableJson<T>(T value) => JsonConvert.SerializeObject(value, Formatting.None);

        private static void TruncateNativePosition(NetworkVector3 value)
        {
            if (value == null) return;
            value.X = Truncate(value.X); value.Y = Truncate(value.Y); value.Z = Truncate(value.Z);
        }

        private static float Truncate(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidOperationException("combat-save-restore-position-not-finite");
            // Native party/area JSON stores positions at a coarser precision and
            // attached entities can be re-snapped by one centimetre on load.
            // Truncate instead of rounding: a native one-centimetre snap across
            // a x.x5 boundary (for example -66.35 -> -66.34) must remain in the
            // same restore bucket.
            try { return (float)(decimal.Truncate((decimal)value * 10m) / 10m); }
            catch (OverflowException) { throw new InvalidOperationException("combat-save-restore-position-out-of-range"); }
        }
    }
}
