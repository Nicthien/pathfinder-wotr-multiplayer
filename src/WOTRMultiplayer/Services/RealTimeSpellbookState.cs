using System;
using System.Collections.Generic;
using System.Linq;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services
{
    public static class RealTimeSpellbookState
    {
        // Only availability/counts may differ. Do not repair book/slot topology by
        // calling Memorize, Rest or Spend: those execute mechanics and callbacks.
        public static string Validate(IEnumerable<NetworkSpellbookState> expected,
            IEnumerable<NetworkSpellbookState> actual)
        {
            var host = Index(expected);
            var client = Index(actual);
            if (host == null || client == null) return "invalid-spellbooks";
            foreach (var id in host.Keys.Union(client.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!host.ContainsKey(id)) return "extra-spellbook:" + id;
                if (!client.ContainsKey(id)) return "missing-spellbook:" + id;
                var left = host[id];
                var right = client[id];
                if (left.SpontaneousSlots.Count != right.SpontaneousSlots.Count)
                    return "spellbook-levels:" + id;
                // Native PostLoad may pad m_MemorizedSpells with empty capacity
                // slots. They contain no spell and no spendable availability, so
                // their count is not combat state and is not stable across peers.
                var leftPrepared = left.PreparedSlots.Where(HasSpell).ToList();
                var rightPrepared = right.PreparedSlots.Where(HasSpell).ToList();
                if (leftPrepared.Count != rightPrepared.Count)
                {
                    var remaining = rightPrepared.Select(StableKey).ToList();
                    var missing = new List<(int Level, int Type, string Spell, int Metamagic,
                        int SpellLevelCost, int HeightenLevel, bool Opposition)>();
                    foreach (var key in leftPrepared.Select(StableKey))
                    {
                        var match = remaining.FindIndex(value => value == key);
                        if (match < 0) missing.Add(key); else remaining.RemoveAt(match);
                    }
                    string Describe((int Level, int Type, string Spell, int Metamagic,
                        int SpellLevelCost, int HeightenLevel, bool Opposition) key) =>
                        key.Level + "/" + key.Type + "/" + key.Spell + "/" + key.Metamagic
                        + "/" + key.SpellLevelCost + "/" + key.HeightenLevel + "/" + key.Opposition;
                    return "spellbook-slots:" + id + ":expected=" + leftPrepared.Count
                        + ";actual=" + rightPrepared.Count
                        + ";missing=" + string.Join(",", missing.Take(4).Select(Describe))
                        + ";extra=" + string.Join(",", remaining.Take(4).Select(Describe));
                }
                // Slot indices are UI ordering, not a stable multiplayer identity. Two
                // peers can legitimately enumerate the same prepared spells at different
                // indices after load/level-up. Compare the per-level spell multiset instead.
                var leftSlots = leftPrepared.GroupBy(StableKey)
                    .ToDictionary(group => group.Key, group => group.Count());
                var rightSlots = rightPrepared.GroupBy(StableKey)
                    .ToDictionary(group => group.Key, group => group.Count());
                if (leftSlots.Count != rightSlots.Count || leftSlots.Any(group =>
                    !rightSlots.TryGetValue(group.Key, out var count) || count != group.Value))
                    return "spellbook-slot-identity:" + id;
            }
            return null;
        }

        public static (int Level, int Index, int Type) Key(NetworkPreparedSpellState slot) =>
            (slot.Level, slot.Index, slot.Type);

        public static (int Level, int Type, string Spell, int Metamagic,
            int SpellLevelCost, int HeightenLevel, bool Opposition) StableKey(
            NetworkPreparedSpellState slot) =>
            (slot.Level, slot.Type, slot.SpellBlueprintId?.ToUpperInvariant(), slot.Metamagic,
                slot.MetamagicSpellLevelCost, slot.MetamagicHeightenLevel, slot.IsOpposition);

        private static bool HasSpell(NetworkPreparedSpellState slot) =>
            !string.IsNullOrWhiteSpace(slot?.SpellBlueprintId);

        private static Dictionary<string, NetworkSpellbookState> Index(IEnumerable<NetworkSpellbookState> books)
        {
            if (books == null) return null;
            var result = new Dictionary<string, NetworkSpellbookState>(StringComparer.OrdinalIgnoreCase);
            foreach (var book in books)
            {
                if (book == null || string.IsNullOrWhiteSpace(book.BlueprintId)
                    || result.ContainsKey(book.BlueprintId) || book.SpontaneousSlots == null
                    || book.SpontaneousSlots.Any(count => count < 0) || book.PreparedSlots == null)
                    return null;
                var slots = new HashSet<(int, int, int)>();
                foreach (var slot in book.PreparedSlots)
                    if (slot == null || slot.Level < 0 || slot.Index < 0 || slot.Type < 0
                        || !slots.Add(Key(slot))) return null;
                result.Add(book.BlueprintId, book);
            }
            return result;
        }
    }
}
