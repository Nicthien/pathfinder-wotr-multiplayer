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
        private Dictionary<string, T> _units = new(StringComparer.Ordinal);
        private long _revision;
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
                _revision++;
            }
        }

        public Dictionary<string, T> Snapshot()
        {
            lock (_lock) return new Dictionary<string, T>(_units, StringComparer.Ordinal);
        }

        // A native checkpoint replaces objects while retaining the encounter
        // identity. Resolve the complete authoritative set before publishing it.
        internal Rebinding RebindCheckpoint(IEnumerable<string> ids, Func<string, T> resolve, Func<bool> authorized)
        {
            if (ids == null || resolve == null || authorized == null) throw new ArgumentNullException();
            long revision;
            lock (_lock) revision = _revision;
            var replacement = new Dictionary<string, T>(StringComparer.Ordinal);
            var uniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                if (!authorized()) throw new InvalidOperationException("native-encounter-rebind-revoked");
                if (string.IsNullOrWhiteSpace(id) || !uniqueIds.Add(id) || replacement.Count >= Limit)
                    throw new InvalidOperationException("invalid-native-encounter-participants");
                replacement.Add(id, resolve(id) ?? throw new InvalidOperationException("native-encounter-unit-missing:" + id));
            }
            lock (_lock)
            {
                if (!authorized() || _revision != revision)
                    throw new InvalidOperationException("native-encounter-rebind-context-changed");
                var previous = _units;
                _units = replacement;
                _revision++;
                return new Rebinding(this, previous, replacement, _revision);
            }
        }

        internal sealed class Rebinding : IDisposable
        {
            private readonly RealTimeEncounterParticipants<T> _owner;
            private readonly Dictionary<string, T> _previous;
            private readonly Dictionary<string, T> _replacement;
            private readonly long _revision;
            private bool _finished;
            internal Rebinding(RealTimeEncounterParticipants<T> owner, Dictionary<string, T> previous,
                Dictionary<string, T> replacement, long revision)
            { _owner = owner; _previous = previous; _replacement = replacement; _revision = revision; }
            internal void Commit()
            {
                lock (_owner._lock)
                {
                    if (_finished || !ReferenceEquals(_owner._units, _replacement) || _owner._revision != _revision)
                        throw new InvalidOperationException("native-encounter-rebind-ownership-changed");
                    _finished = true;
                }
            }
            public void Dispose()
            {
                lock (_owner._lock)
                {
                    if (_finished) return;
                    _finished = true;
                    if (!ReferenceEquals(_owner._units, _replacement) || _owner._revision != _revision)
                        throw new InvalidOperationException("native-encounter-rebind-rollback-conflict");
                    _owner._units = _previous;
                    _owner._revision++;
                }
            }
        }
    }
}
