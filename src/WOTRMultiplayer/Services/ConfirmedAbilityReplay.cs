using System.Runtime.CompilerServices;
using Kingmaker.UnitLogic.Commands.Base;

namespace WOTRMultiplayer.Services
{
    // Exact identities only: no later command by the same caster is authorized.
    internal static class ConfirmedAbilityReplay
    {
        private sealed class Entry { public bool Reported; }
        private static readonly ConditionalWeakTable<UnitCommand, Entry> Commands = new();
        public static void Register(UnitCommand command) => Commands.GetValue(command, _ => new Entry());
        public static bool Contains(UnitCommand command) => Commands.TryGetValue(command, out _);
        public static bool ReportOnce(UnitCommand command)
        {
            if (!Commands.TryGetValue(command, out var entry) || entry.Reported) return false;
            entry.Reported = true;
            return true;
        }
    }
}
