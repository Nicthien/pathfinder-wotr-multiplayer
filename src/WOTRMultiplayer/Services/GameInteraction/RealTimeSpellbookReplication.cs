using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services.GameInteraction
{
    public static class RealTimeSpellbookReplication
    {
        private static readonly AccessTools.FieldRef<SpellSlot, AbilityData> SpellField =
            AccessTools.FieldRefAccess<SpellSlot, AbilityData>("Spell");

        public static List<NetworkSpellbookState> Capture(UnitEntityData unit) =>
            unit.Spellbooks.Select(book => new NetworkSpellbookState
            {
                BlueprintId = book.Blueprint.AssetGuid.ToString(),
                SpontaneousSlots = (book.m_SpontaneousSlots ?? []).ToList(),
                PreparedSlots = (book.m_MemorizedSpells ?? [])
                    .SelectMany((slots, level) => (slots ?? [])
                    .Where(slot => slot?.Spell?.Blueprint != null)
                    .Select(slot => new NetworkPreparedSpellState
                    {
                        Level = level, Index = slot.Index, Type = (int)slot.Type,
                        SpellBlueprintId = slot.Spell?.Blueprint?.AssetGuid.ToString(),
                        Metamagic = (int)(slot.Spell?.MetamagicData?.MetamagicMask ?? 0),
                        MetamagicSpellLevelCost = slot.Spell?.MetamagicData?.SpellLevelCost ?? 0,
                        MetamagicHeightenLevel = slot.Spell?.MetamagicData?.HeightenLevel ?? 0,
                        Available = slot.Available, IsOpposition = slot.IsOpposition
                    })).ToList()
            }).ToList();

        // Native same-area PostLoad occasionally forgets or duplicates prepared
        // spell payloads while leaving the capacity slots intact. Repair only the
        // slot graph through direct fields: Memorize/Forget/Rest would execute UI
        // and gameplay callbacks during a protected reconstruction.
        public static int NormalizeNativeLoadTopology(UnitEntityData unit,
            IEnumerable<NetworkSpellbookState> expectedStates, RealTimeBuffRestoreTransaction transaction)
        {
            if (unit == null || expectedStates == null || transaction == null)
                throw new ArgumentNullException();
            var expected = expectedStates.ToDictionary(state => state.BlueprintId,
                StringComparer.OrdinalIgnoreCase);
            var books = unit.Spellbooks.ToDictionary(book => book.Blueprint.AssetGuid.ToString(),
                StringComparer.OrdinalIgnoreCase);
            var changed = 0;
            foreach (var pair in expected)
            {
                if (!books.TryGetValue(pair.Key, out var book)) continue;
                var state = pair.Value;
                var actualPairs = CapturePreparedSlots(book)
                    .Where(item => !string.IsNullOrWhiteSpace(item.State.SpellBlueprintId)).ToList();
                var remaining = actualPairs.ToList();
                var missing = new List<NetworkPreparedSpellState>();
                foreach (var slot in state.PreparedSlots.Where(HasSpell))
                {
                    var index = remaining.FindIndex(item =>
                        RealTimeSpellbookState.StableKey(item.State)
                        == RealTimeSpellbookState.StableKey(slot));
                    if (index < 0) missing.Add(slot);
                    else remaining.RemoveAt(index);
                }
                if (remaining.Count == 0 && missing.Count == 0) continue;

                var snapshot = new SpellbookTopologySnapshot(book);
                transaction.TrackNativeState(
                    () =>
                    {
                        var error = RealTimeSpellbookState.Validate([state], [Capture(book)]);
                        if (error != null) throw new InvalidOperationException(
                            "native-spellbook-changed-before-commit:" + error);
                    },
                    snapshot.Rollback);

                foreach (var extra in remaining)
                {
                    ClearDirect(extra.Slot);
                    changed++;
                }
                foreach (var missingSlot in missing)
                {
                    RestoreDirect(book, missingSlot);
                    changed++;
                }
                NormalizeOppositionLinks(book, state);
                var validation = RealTimeSpellbookState.Validate([state], [Capture(book)]);
                if (validation != null)
                    throw new InvalidOperationException("native-spellbook-topology-repair-failed:" + validation);
            }
            return changed;
        }

        // Caller preflights every unit before changing any state. Direct field
        // assignments intentionally avoid Spend/Rest/Restore and their handlers.
        public static void ApplyValidated(UnitEntityData unit, IEnumerable<NetworkSpellbookState> states)
        {
            var books = unit.Spellbooks.ToDictionary(book => book.Blueprint.AssetGuid.ToString(),
                StringComparer.OrdinalIgnoreCase);
            foreach (var state in states)
                ApplyBookAvailability(books[state.BlueprintId], state);
        }

        private static void ApplyBookAvailability(Spellbook book, NetworkSpellbookState state)
        {
            for (var level = 0; level < state.SpontaneousSlots.Count; level++)
                book.m_SpontaneousSlots[level] = state.SpontaneousSlots[level];
            var local = CapturePreparedSlots(book)
                .GroupBy(pair => RealTimeSpellbookState.StableKey(pair.State))
                .ToDictionary(group => group.Key,
                    group => new Queue<(SpellSlot Slot, NetworkPreparedSpellState State)>(
                        group.OrderBy(pair => pair.State.Index)));
            foreach (var stateSlot in state.PreparedSlots
                .OrderBy(slot => slot.Level).ThenBy(slot => slot.Type).ThenBy(slot => slot.Index))
                local[RealTimeSpellbookState.StableKey(stateSlot)].Dequeue().Slot.Available = stateSlot.Available;
        }

        private static IEnumerable<(SpellSlot Slot, NetworkPreparedSpellState State)> CapturePreparedSlots(Spellbook book) =>
            (book.m_MemorizedSpells ?? []).SelectMany((values, level) => (values ?? []).Select(slot =>
                (slot, new NetworkPreparedSpellState
                {
                    Level = level,
                    Index = slot.Index,
                    Type = (int)slot.Type,
                    SpellBlueprintId = slot.Spell?.Blueprint?.AssetGuid.ToString(),
                    Metamagic = (int)(slot.Spell?.MetamagicData?.MetamagicMask ?? 0),
                        MetamagicSpellLevelCost = slot.Spell?.MetamagicData?.SpellLevelCost ?? 0,
                        MetamagicHeightenLevel = slot.Spell?.MetamagicData?.HeightenLevel ?? 0,
                    IsOpposition = slot.IsOpposition
                })));

        private static NetworkSpellbookState Capture(Spellbook book) => new()
        {
            BlueprintId = book.Blueprint.AssetGuid.ToString(),
            SpontaneousSlots = (book.m_SpontaneousSlots ?? []).ToList(),
            PreparedSlots = CapturePreparedSlots(book).Select(pair => pair.State)
                .Where(HasSpell).ToList()
        };

        private static bool HasSpell(NetworkPreparedSpellState slot) =>
            !string.IsNullOrWhiteSpace(slot?.SpellBlueprintId);

        private static void ClearDirect(SpellSlot slot)
        {
            SpellField(slot) = null;
            slot.Available = false;
            slot.LinkedSlots = null;
            slot.IsOpposition = false;
        }

        private static SpellSlot RestoreDirect(Spellbook book, NetworkPreparedSpellState state)
        {
            if (state.Level < 0 || state.Level >= book.m_MemorizedSpells.Length
                || !Enum.IsDefined(typeof(SpellSlotType), state.Type)
                || state.MetamagicSpellLevelCost < 0 || state.MetamagicHeightenLevel < 0
                || state.Level - state.MetamagicSpellLevelCost < 0)
                throw new InvalidOperationException("native-spellbook-slot-state-invalid");
            var blueprint = ResourcesLibrary.TryGetBlueprint<BlueprintAbility>(state.SpellBlueprintId)
                ?? throw new InvalidOperationException("native-spellbook-blueprint-unavailable:"
                    + state.SpellBlueprintId);
            var levelSlots = book.m_MemorizedSpells[state.Level]
                ?? throw new InvalidOperationException("native-spellbook-level-unavailable:" + state.Level);
            var type = (SpellSlotType)state.Type;
            var target = levelSlots.FirstOrDefault(slot => SpellField(slot) == null
                && slot.Type == type && slot.Index == state.Index)
                ?? levelSlots.FirstOrDefault(slot => SpellField(slot) == null && slot.Type == type);
            if (target == null)
            {
                var used = new HashSet<int>(levelSlots.Where(slot => slot.Type == type).Select(slot => slot.Index));
                var index = state.Index;
                while (used.Contains(index)) index++;
                target = new SpellSlot(state.Level, type, index, book);
                levelSlots.Add(target);
                levelSlots.Sort();
            }
            var ability = new AbilityData(blueprint, book, state.Level - state.MetamagicSpellLevelCost);
            if (state.Metamagic != 0 || state.MetamagicSpellLevelCost != 0
                || state.MetamagicHeightenLevel != 0)
            {
                var metamagic = new MetamagicData
                {
                    SpellLevelCost = state.MetamagicSpellLevelCost,
                    HeightenLevel = state.MetamagicHeightenLevel
                };
                metamagic.Add((Metamagic)state.Metamagic);
                ability.MetamagicData = metamagic;
            }
            SpellField(target) = ability;
            target.Available = state.Available;
            target.IsOpposition = state.IsOpposition;
            target.LinkedSlots = null;
            return target;
        }

        private static void NormalizeOppositionLinks(Spellbook book, NetworkSpellbookState state)
        {
            var expectedOpposition = state.PreparedSlots.Where(slot => HasSpell(slot) && slot.IsOpposition)
                .GroupBy(RealTimeSpellbookState.StableKey).ToDictionary(group => group.Key, group => group.Count());
            foreach (var pair in CapturePreparedSlots(book).Where(item => HasSpell(item.State)).ToList())
                if (!pair.State.IsOpposition) pair.Slot.LinkedSlots = null;
            foreach (var group in CapturePreparedSlots(book).Where(item => item.State.IsOpposition)
                         .GroupBy(item => RealTimeSpellbookState.StableKey(item.State)))
            {
                if (!expectedOpposition.TryGetValue(group.Key, out var expectedCount)
                    || expectedCount != group.Count() || expectedCount % 2 != 0)
                    throw new InvalidOperationException("native-spellbook-opposition-links-invalid");
                var slots = group.OrderBy(item => item.State.Index).Select(item => item.Slot).ToList();
                for (var index = 0; index < slots.Count; index += 2)
                {
                    var linked = new[] { slots[index], slots[index + 1] };
                    slots[index].LinkedSlots = linked;
                    slots[index + 1].LinkedSlots = linked;
                }
            }
        }

        private sealed class SpellbookTopologySnapshot
        {
            private readonly Spellbook _book;
            private readonly List<SpellSlot>[] _levels;
            private readonly Dictionary<SpellSlot, (AbilityData Spell, bool Available,
                SpellSlot[] Linked, bool Opposition)> _slots;
            private readonly List<AbilityData> _removed;

            public SpellbookTopologySnapshot(Spellbook book)
            {
                _book = book;
                _levels = book.m_MemorizedSpells.Select(level => level?.ToList()).ToArray();
                _slots = _levels.Where(level => level != null).SelectMany(level => level).Distinct()
                    .ToDictionary(slot => slot, slot => (SpellField(slot), slot.Available,
                        slot.LinkedSlots?.ToArray(), slot.IsOpposition));
                _removed = book.RemoveMemorizedSpells.ToList();
            }

            public void Rollback()
            {
                for (var level = 0; level < _levels.Length; level++)
                {
                    _book.m_MemorizedSpells[level].Clear();
                    if (_levels[level] != null) _book.m_MemorizedSpells[level].AddRange(_levels[level]);
                }
                foreach (var pair in _slots)
                {
                    SpellField(pair.Key) = pair.Value.Spell;
                    pair.Key.Available = pair.Value.Available;
                    pair.Key.LinkedSlots = pair.Value.Linked?.ToArray();
                    pair.Key.IsOpposition = pair.Value.Opposition;
                }
                _book.RemoveMemorizedSpells.Clear();
                _book.RemoveMemorizedSpells.AddRange(_removed);
            }
        }
    }
}
