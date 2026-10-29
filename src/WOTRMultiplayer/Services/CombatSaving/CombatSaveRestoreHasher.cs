using System;
using System.Collections.Generic;
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
                    .Where(buff => !IsAreaDerived(buff, areas) && !IsNativeLoadDerived(buff))
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

        // A native load recreates attached areas and their derived buffs with
        // fresh runtime ids.  Reapply the saved mechanical state while retaining
        // only those freshly derived objects from the loaded world.  Everything
        // else (damage, resources, temporary buffs, engagements and position)
        // remains authoritative from the save manifest.
        internal static NetworkCombatState PrepareLoadedWorldApplication(
            NetworkCombatState saved, NetworkCombatState loaded)
        {
            if (saved?.Units == null || saved.AreaEffects == null
                || loaded?.Units == null || loaded.AreaEffects == null)
                throw new ArgumentNullException(saved == null ? nameof(saved) : nameof(loaded));
            var expected = JsonConvert.DeserializeObject<NetworkCombatState>(
                JsonConvert.SerializeObject(saved))
                ?? throw new InvalidOperationException("combat-save-loaded-application-clone-failed");
            var loadedById = loaded.Units.Where(unit => unit != null)
                .ToDictionary(unit => unit.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var unit in expected.Units.Where(unit => unit != null))
            {
                if (!loadedById.TryGetValue(unit.Id, out var current))
                    throw new InvalidOperationException("combat-save-loaded-unit-missing:" + unit.Id);
                var savedBuffs = unit.BuffCollection?.Buffs ?? [];
                var derived = (current.BuffCollection?.Buffs ?? [])
                    .Where(buff => IsAreaDerived(buff, loaded.AreaEffects) || IsNativeLoadDerived(buff));
                if (unit.BuffCollection != null)
                    unit.BuffCollection.Buffs = savedBuffs
                        .Where(buff => !IsAreaDerived(buff, saved.AreaEffects) && !IsNativeLoadDerived(buff))
                        .Concat(derived)
                        .GroupBy(buff => buff?.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First()).ToList();
            }
            expected.AreaEffects = JsonConvert.DeserializeObject<List<Entities.AreaEffects.NetworkAreaEffect>>(
                JsonConvert.SerializeObject(loaded.AreaEffects)) ?? [];
            expected.ProjectilesCaptured = loaded.ProjectilesCaptured;
            expected.Projectiles = JsonConvert.DeserializeObject<List<NetworkProjectileState>>(
                JsonConvert.SerializeObject(loaded.Projectiles)) ?? [];
            return expected;
        }

        internal static bool IsAreaDerived(Entities.Units.NetworkBuff buff,
            System.Collections.Generic.IEnumerable<Entities.AreaEffects.NetworkAreaEffect> areas)
        {
            if (buff?.ContextChain == null || string.IsNullOrWhiteSpace(buff.CasterId)) return false;
            return areas.Any(area => area != null
                && string.Equals(area.CasterUnitId, buff.CasterId, StringComparison.Ordinal)
                && buff.ContextChain.Any(context => context != null
                    && string.Equals(context.BlueprintId, area.BlueprintId, StringComparison.OrdinalIgnoreCase)));
        }

        internal static bool IsNativeLoadDerived(Entities.Units.NetworkBuff buff)
        {
            // Owlcat rebuilds this hidden AI/group countdown from combat-group
            // state instead of serializing the fact itself. Its presence before
            // save and absence during the paused post-load barrier are therefore
            // two representations of the same native mechanics.
            return string.Equals(buff?.BlueprintId, "46b39651560c13f41a1f5deebb9caa1a",
                       StringComparison.OrdinalIgnoreCase)
                // Toggle marker for the inspired-rage aura. Native loading may
                // recreate it on party members immediately and on NPCs only
                // after the held world resumes; the aura itself stays protected.
                || string.Equals(buff?.BlueprintId, "9fd2dd27d838f0049ab56e9da3508b25",
                    StringComparison.OrdinalIgnoreCase);
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
