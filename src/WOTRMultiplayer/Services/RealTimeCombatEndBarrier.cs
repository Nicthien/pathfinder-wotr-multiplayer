using System;
using System.Collections.Generic;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Services
{
    // Missing peers are never silently removed to authorize a final save.
    public sealed class RealTimeCombatEndBarrier
    {
        private readonly HashSet<long> _required;
        private readonly HashSet<long> _ready = [];
        public RealTimeCombatEndStage Stage { get; private set; } = RealTimeCombatEndStage.Prepare;
        public ulong Hash { get; }
        public bool Complete { get; private set; }

        public RealTimeCombatEndBarrier(IEnumerable<long> peers, ulong hash)
        {
            _required = new HashSet<long>(peers ?? throw new ArgumentNullException(nameof(peers)));
            if (_required.Count == 0) throw new InvalidOperationException("combat-end-requires-peers");
            Hash = hash;
        }

        public bool Acknowledge(long peer, int stage, ulong hash, bool applied)
        {
            if (Complete || !_required.Contains(peer) || stage != (int)Stage || !applied || hash != Hash)
                return false;
            _ready.Add(peer);
            if (!_required.IsSubsetOf(_ready)) return false;
            _ready.Clear();
            if (Stage == RealTimeCombatEndStage.Finalize) Complete = true;
            else Stage++;
            return true;
        }
    }
}
