using System;
using System.Collections.Generic;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Services
{
    // Used under the client's game-event gate. A frame is committed only after
    // presentation succeeds, not merely because a newer frame arrived.
    public sealed class RealTimePresentationInbox
    {
        private readonly SortedDictionary<long, NotifyRealTimeAttackPresentation> _pending = new();
        private readonly object _gate = new();
        public long AppliedSequence { get; private set; }
        private DateTime? _gapSince;

        public bool Enqueue(NotifyRealTimeAttackPresentation frame)
        {
            lock (_gate)
            {
                if (frame == null || frame.Sequence <= 0) return false;
                if (frame.Sequence <= AppliedSequence || _pending.ContainsKey(frame.Sequence)) return true;
                if (frame.Sequence - AppliedSequence > 128 || _pending.Count >= 128) return false;
                _pending.Add(frame.Sequence, frame);
                if (!_pending.ContainsKey(AppliedSequence + 1)) _gapSince ??= DateTime.UtcNow;
                return true;
            }
        }

        public bool TryPeek(out NotifyRealTimeAttackPresentation frame)
        {
            lock (_gate) return _pending.TryGetValue(AppliedSequence + 1, out frame);
        }

        public void Commit(long sequence)
        {
            lock (_gate)
            {
                if (sequence != AppliedSequence + 1 || !_pending.Remove(sequence))
                    throw new InvalidOperationException("Presentation frame was not next in sequence.");
                AppliedSequence = sequence;
                _gapSince = _pending.Count > 0 && !_pending.ContainsKey(sequence + 1)
                    ? _gapSince ?? DateTime.UtcNow : null;
            }
        }

        public bool HasExpiredGap(DateTime now)
        {
            lock (_gate) return _gapSince.HasValue
                && now - _gapSince.Value > TimeSpan.FromSeconds(5);
        }

        public void RestoreBaseline(long includedSequence)
        {
            if (includedSequence < 0) throw new ArgumentOutOfRangeException(nameof(includedSequence));
            lock (_gate)
            {
                if (includedSequence <= AppliedSequence) return;
                foreach (var sequence in new List<long>(_pending.Keys))
                    if (sequence <= includedSequence) _pending.Remove(sequence);
                AppliedSequence = includedSequence;
                _gapSince = _pending.Count > 0 && !_pending.ContainsKey(AppliedSequence + 1)
                    ? DateTime.UtcNow : null;
            }
        }
    }
}
