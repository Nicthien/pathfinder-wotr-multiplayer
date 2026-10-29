using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace WOTRMultiplayer.Services
{
    /// <summary>Matches host-generated entity IDs to client spawns in the same combat epoch.</summary>
    public sealed class RealTimeEntityIdMailbox
    {
        private const int MaxBufferedIds = 128;
        private readonly object _sync = new();
        private readonly Dictionary<string, long> _nextSequence = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _consumedSequence = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Key, long Sequence), string> _ids = [];
        private long _epoch = long.MinValue;
        private long _generation;

        public void Reset()
        {
            lock (_sync)
            {
                _epoch = long.MinValue;
                _generation++;
                _nextSequence.Clear();
                _consumedSequence.Clear();
                _ids.Clear();
                Monitor.PulseAll(_sync);
            }
        }

        public long Publish(long epoch, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Entity key is required", nameof(key));
            lock (_sync)
            {
                if (!AdvanceEpoch(epoch)) throw new InvalidOperationException("Stale entity-ID epoch");
                return NextSequence(key);
            }
        }

        public bool Receive(long epoch, string key, long sequence, string entityId)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(entityId) || sequence <= 0)
                return false;
            lock (_sync)
            {
                if (!AdvanceEpoch(epoch)) return false;
                if (_consumedSequence.TryGetValue(key, out var consumed) && sequence <= consumed)
                    return false;
                var id = (key, sequence);
                if (_ids.TryGetValue(id, out var previous))
                    return string.Equals(previous, entityId, StringComparison.Ordinal);
                if (_ids.Count >= MaxBufferedIds) return false;
                _ids.Add(id, entityId);
                Monitor.PulseAll(_sync);
                return true;
            }
        }

        public bool TryConsume(long epoch, string key, int timeoutMs, out long sequence, out string entityId)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Entity key is required", nameof(key));
            if (timeoutMs < 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            lock (_sync)
            {
                if (!AdvanceEpoch(epoch))
                {
                    sequence = 0;
                    entityId = null;
                    return false;
                }
                sequence = NextSequence(key);
                var generation = _generation;
                var watch = Stopwatch.StartNew();
                while (_epoch == epoch && _generation == generation)
                {
                    if (_ids.TryGetValue((key, sequence), out entityId))
                    {
                        _ids.Remove((key, sequence));
                        _consumedSequence[key] = sequence;
                        return true;
                    }
                    var remaining = timeoutMs - (int)watch.ElapsedMilliseconds;
                    if (remaining <= 0) break;
                    Monitor.Wait(_sync, remaining);
                }
                if (_epoch == epoch && _generation == generation) _consumedSequence[key] = sequence;
                entityId = null;
                return false;
            }
        }

        private bool AdvanceEpoch(long epoch)
        {
            if (epoch < _epoch) return false;
            if (epoch == _epoch) return true;
            _epoch = epoch;
            _generation++;
            _nextSequence.Clear();
            _consumedSequence.Clear();
            _ids.Clear();
            Monitor.PulseAll(_sync);
            return true;
        }

        private long NextSequence(string key)
        {
            var sequence = _nextSequence.TryGetValue(key, out var previous) ? previous + 1 : 1;
            _nextSequence[key] = sequence;
            return sequence;
        }
    }
}
