using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services.GameInteraction
{
    public static class RealTimeSpellbookReplication
    {
        public static List<NetworkSpellbookState> Capture(UnitEntityData unit) =>
            unit.Spellbooks.Select(book => new NetworkSpellbookState
            {
                BlueprintId = book.Blueprint.AssetGuid.ToString(),
                SpontaneousSlots = (book.m_SpontaneousSlots ?? []).ToList(),
                PreparedSlots = (book.m_MemorizedSpells ?? [])
                    .SelectMany((slots, level) => (slots ?? []).Select(slot => new NetworkPreparedSpellState
                    {
                        Level = level, Index = slot.Index, Type = (int)slot.Type,
                        SpellBlueprintId = slot.Spell?.Blueprint?.AssetGuid.ToString(),
                        Metamagic = (int)(slot.Spell?.MetamagicData?.MetamagicMask ?? 0),
                        Available = slot.Available, IsOpposition = slot.IsOpposition
                    })).ToList()
            }).ToList();

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
            var slots = (book.m_MemorizedSpells ?? [])
                .SelectMany((values, level) => (values ?? [])
                    .Select(slot => (Key: (level, slot.Index, (int)slot.Type), Slot: slot)))
                .ToDictionary(pair => pair.Key, pair => pair.Slot);
            foreach (var stateSlot in state.PreparedSlots)
                slots[RealTimeSpellbookState.Key(stateSlot)].Available = stateSlot.Available;
        }
    }
}
