using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace WOTRMultiplayer.Services
{
    // Losing combat membership (death, unconsciousness, withdrawal) is not an
    // entity deletion. Reference states must still include those final results.
    // Weak encounter keys prevent participants leaking into another combat/load.
    public sealed class RealTimeEncounterParticipants<T> where T : class
    {
        private static readonly ConditionalWeakTable<object, RealTimeEncounterParticipants<T>> Encounters = new();
        private readonly Dictionary<string, T> _units = new(StringComparer.Ordinal);
        private readonly object _lock = new();
        private const int Limit = 4096;

        public static RealTimeEncounterParticipants<T> For(object encounter) => encounter == null ? null
            : Encounters.GetValue(encounter, _ => new RealTimeEncounterParticipants<T>());

        public void Remember(string id, T unit)
        {
            if (string.IsNullOrWhiteSpace(id) || unit == null)
                throw new InvalidOperationException("invalid-encounter-participant");
            lock (_lock)
            {
                if (_units.TryGetValue(id, out var previous))
                {
                    if (!ReferenceEquals(previous, unit))
                        throw new InvalidOperationException("conflicting-encounter-participant:" + id);
                    return;
                }
                if (_units.Count >= Limit) throw new InvalidOperationException("encounter-participant-limit");
                _units.Add(id, unit);
            }
        }

        public Dictionary<string, T> Snapshot()
        {
            lock (_lock) return new Dictionary<string, T>(_units, StringComparer.Ordinal);
        }
    }
}
