using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services
{
    /// <summary>Canonical, order-independent fingerprint of the state needed by the RTwP repair protocol.</summary>
    public static class RealTimeCombatHasher
    {
        public static ulong Compute(RealTimeCombatSnapshot snapshot)
        {
            if (snapshot?.Combat == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(5); // Hash schema version, distinct from the wire protocol version.
                writer.Write(snapshot.RandomState);

                var units = (snapshot.Combat.Units ?? []).OrderBy(x => x?.Id, StringComparer.Ordinal).ToList();
                writer.Write(units.Count);
                foreach (var unit in units)
                {
                    WriteString(writer, unit?.Id);
                    WriteString(writer, unit?.BlueprintId);
                    WriteString(writer, unit?.OriginalBlueprintId);
                    WriteString(writer, unit?.CasterUnitId);
                    WriteString(writer, unit?.SourceAbilityBlueprintId);
                    WriteString(writer, unit?.MasterUnitId);
                    WriteString(writer, unit?.GroupId);
                    WriteVector(writer, unit?.Position);
                    writer.Write(Quantize(unit?.Orientation ?? 0));
                    writer.Write(unit?.Descriptor?.Damage ?? 0);
                    writer.Write(unit?.Descriptor?.Stats?.DamageNonLethal ?? 0);
                    writer.Write(unit?.Descriptor?.State?.IsCharging ?? false);
                    writer.Write(unit?.Descriptor?.State?.Prone?.Active ?? false);
                    writer.Write(unit?.Descriptor?.State?.Prone?.ShouldBeActive ?? false);
                    writer.Write(unit?.CombatState?.NotSurprised ?? false);
                    WriteEngagements(writer, unit?.CombatState?.EngagedUnits);
                    WriteEngagements(writer, unit?.CombatState?.EngagedBy);

                    var buffs = (unit?.BuffCollection?.Buffs ?? [])
                        .OrderBy(x => x?.Id, StringComparer.Ordinal).ToList();
                    writer.Write(buffs.Count);
                    foreach (var buff in buffs)
                    {
                        WriteString(writer, buff?.Id);
                        WriteString(writer, buff?.BlueprintId);
                        WriteString(writer, buff?.CasterId);
                        writer.Write(buff?.Rank ?? 0);
                        writer.Write(buff?.IsPermanent ?? false);
                    }
                }

                var areas = (snapshot.Combat.AreaEffects ?? []).OrderBy(x => x?.Id, StringComparer.Ordinal).ToList();
                writer.Write(areas.Count);
                foreach (var area in areas)
                {
                    WriteString(writer, area?.Id);
                    WriteString(writer, area?.BlueprintId);
                    WriteString(writer, area?.CasterUnitId);
                    WriteString(writer, area?.OwnerUnitId);
                    WriteString(writer, area?.SourceAbilityBlueprintId);
                    WriteVector(writer, area?.Position);
                    writer.Write((int)(area?.Type ?? 0));
                    var inside = (area?.UnitsInside ?? []).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    writer.Write(inside.Count);
                    foreach (var id in inside)
                    {
                        WriteString(writer, id);
                    }
                }

                var orders = (snapshot.ActiveOrders ?? [])
                    .OrderBy(x => x?.PlayerId)
                    .ThenBy(x => x?.Sequence)
                    .ThenBy(x => x?.UnitId, StringComparer.Ordinal).ToList();
                writer.Write(orders.Count);
                foreach (var order in orders)
                {
                    writer.Write(order?.Epoch ?? 0);
                    writer.Write(order?.Sequence ?? 0);
                    writer.Write(order?.PlayerId ?? 0);
                    WriteString(writer, order?.UnitId);
                    writer.Write((int)(order?.Kind ?? 0));
                    WriteString(writer, order?.Payload);
                }
            }

            using var sha = SHA256.Create();
            var digest = sha.ComputeHash(stream.ToArray());
            ulong result = 0;
            for (var i = 0; i < sizeof(ulong); i++)
            {
                result |= (ulong)digest[i] << (8 * i);
            }
            return result;
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            writer.Write(value != null);
            if (value != null)
            {
                writer.Write(value);
            }
        }

        private static void WriteVector(BinaryWriter writer, NetworkVector3 position)
        {
            writer.Write(position != null);
            if (position != null)
            {
                writer.Write(Quantize(position.X));
                writer.Write(Quantize(position.Y));
                writer.Write(Quantize(position.Z));
            }
        }

        private static void WriteEngagements(BinaryWriter writer, System.Collections.Generic.IEnumerable<NetworkUnitEngagement> engagements)
        {
            var ordered = (engagements ?? [])
                .OrderBy(x => x?.UnitId, StringComparer.Ordinal)
                .ToList();
            writer.Write(ordered.Count);
            foreach (var engagement in ordered)
            {
                WriteString(writer, engagement?.UnitId);
            }
        }

        private static long Quantize(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new InvalidOperationException("A combat fingerprint cannot contain a non-finite coordinate.");
            }
            return checked((long)Math.Round(value * 1000d, MidpointRounding.AwayFromZero));
        }
    }
}
