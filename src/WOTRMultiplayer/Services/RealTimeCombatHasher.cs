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
                writer.Write(15); // Hash schema version, distinct from the wire protocol version.
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
                    WriteString(writer, unit?.SceneName);
                    WriteVector(writer, unit?.Position);
                    writer.Write(Quantize(unit?.Orientation ?? 0));
                    writer.Write(unit?.Descriptor?.Damage ?? 0);
                    writer.Write(unit?.Descriptor?.Stats?.DamageNonLethal ?? 0);
                    var vitals = unit?.Descriptor?.Vitals;
                    writer.Write(vitals != null);
                    if (vitals != null)
                    {
                        writer.Write(vitals.Damage); writer.Write(vitals.LifeState); writer.Write(vitals.IsFinallyDead);
                        writer.Write(vitals.IsInCombat); writer.Write(vitals.GroupCounterCaptured);
                        if (vitals.GroupCounterCaptured) writer.Write(vitals.GroupCombatCounter);
                        WriteStat(writer, vitals.HitPoints); WriteStat(writer, vitals.TemporaryHitPoints); WriteStat(writer, vitals.NonLethal);
                    }
                    writer.Write(unit?.Descriptor?.ResourcesCaptured ?? false);
                    var calculatedStats = unit?.Descriptor?.CalculatedStats;
                    var calculatedError = RealTimeCalculatedStatState.ValidateValues(calculatedStats);
                    if (calculatedError != null) throw new InvalidOperationException(calculatedError);
                    writer.Write(calculatedStats != null);
                    if (calculatedStats != null)
                    {
                        writer.Write(calculatedStats.Count);
                        foreach (var stat in calculatedStats.OrderBy(stat => stat.Type))
                        {
                            writer.Write(stat.Type);
                            WriteStat(writer, stat.Values);
                        }
                    }
                    if (unit?.Descriptor?.ResourcesCaptured == true)
                    {
                        var resources = (unit.Descriptor.Resources ?? [])
                            .OrderBy(resource => resource?.BlueprintId, StringComparer.Ordinal).ToList();
                        writer.Write(resources.Count);
                        foreach (var resource in resources)
                        {
                            WriteString(writer, resource?.BlueprintId);
                            writer.Write(resource?.Amount ?? 0);
                            writer.Write(resource?.RetainCount ?? 0);
                        }
                    }
                    writer.Write(unit?.Descriptor?.SpellbooksCaptured ?? false);
                    if (unit?.Descriptor?.SpellbooksCaptured == true)
                    {
                        var books = (unit.Descriptor.Spellbooks ?? [])
                            .OrderBy(book => book?.BlueprintId, StringComparer.Ordinal).ToList();
                        writer.Write(books.Count);
                        foreach (var book in books)
                        {
                            WriteString(writer, book?.BlueprintId);
                            var spontaneous = book?.SpontaneousSlots ?? [];
                            writer.Write(spontaneous.Count);
                            foreach (var count in spontaneous) writer.Write(count);
                            var prepared = (book?.PreparedSlots ?? []).OrderBy(slot => slot?.Level)
                                .ThenBy(slot => slot?.Index).ThenBy(slot => slot?.Type).ToList();
                            writer.Write(prepared.Count);
                            foreach (var slot in prepared)
                            {
                                writer.Write(slot?.Level ?? -1);
                                writer.Write(slot?.Index ?? -1);
                                writer.Write(slot?.Type ?? -1);
                                WriteString(writer, slot?.SpellBlueprintId);
                                writer.Write(slot?.Metamagic ?? 0);
                                writer.Write(slot?.Available ?? false);
                                writer.Write(slot?.IsOpposition ?? false);
                            }
                        }
                    }
                    writer.Write(unit?.Descriptor?.State?.IsCharging ?? false);
                    writer.Write(unit?.Descriptor?.State?.IsFinallyDead ?? false);
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
                        writer.Write(buff?.IsHidden ?? false);
                        WriteString(writer, buff?.SourceAbilityCasterId);
                        WriteString(writer, buff?.SourceAbility?.BlueprintId);
                        WriteBuffContext(writer, buff?.ContextChain);
                    }

                    var negativeLevels = (unit?.BuffCollection?.NegativeLevels ?? [])
                        .OrderBy(x => x?.Count)
                        .ThenBy(x => x?.SavingThrowType)
                        .ThenBy(x => x?.EnergyDrainType)
                        .ToList();
                    writer.Write(negativeLevels.Count);
                    foreach (var level in negativeLevels)
                    {
                        writer.Write(level?.Count ?? 0);
                        writer.Write(level == null ? 0 : (int)level.SavingThrowType);
                        writer.Write(level == null ? 0 : (int)level.EnergyDrainType);
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
                    WriteString(writer, area?.SceneName);
                    WriteString(writer, area?.TargetUnitId);
                    WriteVector(writer, area?.TargetPoint);
                    writer.Write(area?.TargetOrientation != null);
                    if (area?.TargetOrientation != null) writer.Write(Quantize(area.TargetOrientation.Value));
                    writer.Write(area?.IsAttachedToUnit ?? false);
                    WriteVector(writer, area?.Position);
                    writer.Write((int)(area?.Type ?? 0));
                    var inside = (area?.UnitsInside ?? []).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    writer.Write(inside.Count);
                    foreach (var id in inside)
                    {
                        WriteString(writer, id);
                    }
                }

                writer.Write(snapshot.Combat.ProjectilesCaptured);
                if (snapshot.Combat.ProjectilesCaptured)
                {
                    var projectiles = (snapshot.Combat.Projectiles ?? []).OrderBy(state => state?.Id, StringComparer.Ordinal).ToList();
                    writer.Write(projectiles.Count);
                    foreach (var projectile in projectiles)
                    {
                        WriteString(writer, projectile?.Id);
                        WriteString(writer, projectile?.BlueprintId);
                        WriteVector(writer, projectile?.Position);
                        writer.Write(Quantize(projectile?.RotationX ?? 0));
                        writer.Write(Quantize(projectile?.RotationY ?? 0));
                        writer.Write(Quantize(projectile?.RotationZ ?? 0));
                        writer.Write(Quantize(projectile?.RotationW ?? 0));
                        writer.Write(projectile?.IsHit ?? false);
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

        private static void WriteBuffContext(BinaryWriter writer, System.Collections.Generic.List<NetworkBuffContextState> chain)
        {
            var error = RealTimeBuffContextChain.Validate(chain);
            if (error != null) throw new InvalidOperationException(error);
            writer.Write(chain != null);
            if (chain == null) return;
            writer.Write(chain.Count);
            foreach (var node in chain)
            {
                WriteString(writer, node.BlueprintId); WriteString(writer, node.CasterId); WriteString(writer, node.OwnerId);
                writer.Write(node.IsAbilityContext);
                writer.Write(node.SourceAbility != null);
                if (node.SourceAbility != null)
                {
                    var ability = node.SourceAbility;
                    WriteString(writer, ability.Id); WriteString(writer, ability.BlueprintId); WriteString(writer, ability.SpellbookId);
                    WriteString(writer, ability.ConvertedFromId); writer.Write(ability.SpellLevel);
                    writer.Write(ability.Metamagic.HasValue); if (ability.Metamagic.HasValue) writer.Write(ability.Metamagic.Value);
                    writer.Write(ability.ParamSpellLevel.HasValue); if (ability.ParamSpellLevel.HasValue) writer.Write(ability.ParamSpellLevel.Value);
                    WriteString(writer, ability.ParamSpellBookId); writer.Write(ability.PotionForOther);
                    WriteString(writer, ability.SourceItem?.Id); WriteString(writer, ability.SourceItem?.BlueprintId);
                    writer.Write(ability.ParamSpellSlot != null);
                    if (ability.ParamSpellSlot != null)
                    {
                        WriteString(writer, ability.ParamSpellSlot.SpellbookId); writer.Write(ability.ParamSpellSlot.SpellLevel);
                        writer.Write(ability.ParamSpellSlot.Slot != null);
                        if (ability.ParamSpellSlot.Slot != null)
                        {
                            writer.Write(ability.ParamSpellSlot.Slot.Index); writer.Write((int)ability.ParamSpellSlot.Slot.Type);
                        }
                    }
                }
                var parameters = node.Params;
                writer.Write(parameters.CasterLevel); writer.Write(parameters.SpellLevel); writer.Write(parameters.Concentration);
                writer.Write(parameters.DC); WriteString(writer, parameters.SpellSource);
                writer.Write(parameters.Metamagic); writer.Write(parameters.RankBonus);
                foreach (var values in new[] { node.Ranks, node.SharedValues, node.Properties })
                {
                    writer.Write(values.Count);
                    foreach (var value in values) writer.Write(value);
                }
                writer.Write(node.SpellDescriptor); writer.Write(node.SpellSchool); writer.Write(node.SpellLevel);
                writer.Write(node.DisableFx); WriteVector(writer, node.Direction); WriteString(writer, node.SourceItemId);
                writer.Write(node.MainTarget != null);
                if (node.MainTarget != null)
                {
                    WriteString(writer, node.MainTarget.UnitId); WriteVector(writer, node.MainTarget.Point);
                    writer.Write(node.MainTarget.Orientation.HasValue);
                    if (node.MainTarget.Orientation.HasValue) writer.Write(node.MainTarget.Orientation.Value);
                }
            }
        }

        private static void WriteStat(BinaryWriter writer, NetworkStatValues stat)
        {
            writer.Write(stat != null);
            if (stat == null) return;
            writer.Write(stat.BaseValue); writer.Write(stat.ModifiedValue);
            writer.Write(stat.ModifiedValueRaw); writer.Write(stat.PermanentValue);
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
