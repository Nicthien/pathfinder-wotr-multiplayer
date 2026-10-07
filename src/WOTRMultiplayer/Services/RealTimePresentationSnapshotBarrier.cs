using System;
using System.IO;
using System.Linq;
using ProtoBuf;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Services
{
    // A normal snapshot cannot certify or replace missing durable/action events.
    // Keep at most the latest snapshot, until its event boundary is actually applied.
    public sealed class RealTimePresentationSnapshotBarrier
    {
        private readonly string _session;
        private readonly int _save;
        private readonly long _epoch;
        private readonly object _gate = new();
        private NotifyRealTimeCombatSnapshot _pending;
        private DateTime? _waitingSince;

        public RealTimePresentationSnapshotBarrier(string session, int save, long epoch)
        { _session = session; _save = save; _epoch = epoch; }

        public NotifyRealTimeCombatSnapshot Offer(NotifyRealTimeCombatSnapshot snapshot, long appliedEventSequence, DateTime now)
        {
            if (snapshot == null || snapshot.SessionId != _session || snapshot.LoadedSaveSeed != _save
                || snapshot.Epoch != _epoch || snapshot.Sequence <= 0 || snapshot.IncludedEventSequence < 0
                || appliedEventSequence < 0 || snapshot.State?.Units == null || snapshot.State.AreaEffects == null
                || snapshot.State.Projectiles == null || snapshot.State.Units.Count > 4096
                || snapshot.State.AreaEffects.Count > 16384 || snapshot.State.Projectiles.Count > 4096)
                throw new InvalidOperationException("presentation-snapshot-boundary-invalid");
            lock (_gate)
            {
                DropObsolete(appliedEventSequence);
                if (_pending != null)
                {
                    if (snapshot.Sequence < _pending.Sequence) return null;
                    if (snapshot.Sequence == _pending.Sequence)
                    {
                        using var left = new MemoryStream(); using var right = new MemoryStream();
                        Serializer.Serialize(left, _pending); Serializer.Serialize(right, snapshot);
                        if (!left.ToArray().SequenceEqual(right.ToArray()))
                            throw new InvalidOperationException("presentation-snapshot-conflicting-duplicate");
                    }
                    else if (snapshot.IncludedEventSequence < _pending.IncludedEventSequence)
                        throw new InvalidOperationException("presentation-snapshot-boundary-regressed");
                }
                // Applying an older checkpoint would overwrite results already received.
                if (snapshot.IncludedEventSequence < appliedEventSequence) return null;
                if (snapshot.IncludedEventSequence == appliedEventSequence)
                { _pending = null; _waitingSince = null; return snapshot; }
                _pending = Serializer.DeepClone(snapshot);
                _waitingSince ??= now; // New snapshots must not prolong a missing event indefinitely.
                return null;
            }
        }

        public NotifyRealTimeCombatSnapshot TakeReady(long appliedEventSequence)
        {
            lock (_gate)
            {
                DropObsolete(appliedEventSequence);
                if (_pending == null || _pending.IncludedEventSequence != appliedEventSequence) return null;
                var result = _pending; _pending = null; _waitingSince = null;
                return result;
            }
        }

        private void DropObsolete(long appliedEventSequence)
        {
            if (_pending != null && _pending.IncludedEventSequence < appliedEventSequence)
            { _pending = null; _waitingSince = null; }
        }

        public bool HasExpiredWait(DateTime now)
        {
            lock (_gate) return _pending != null && _waitingSince.HasValue
                && now - _waitingSince.Value > TimeSpan.FromSeconds(5);
        }
    }
}
