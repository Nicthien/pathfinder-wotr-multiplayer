using System.Collections.Generic;

namespace WOTRMultiplayer.Entities.Combat.RealTime
{
    public class NetworkRealTimeCombatState
    {
        public const int ProtocolVersion = 3;

        public CombatMode Mode { get; set; } = CombatMode.TurnBased;

        public CombatMode? PendingMode { get; set; }

        public PausePolicy PausePolicy { get; set; } = PausePolicy.AnyoneConsensusResume;

        public CombatSynchronizationPhase Phase { get; set; } = CombatSynchronizationPhase.Stable;

        public long Epoch { get; set; }

        public long LastAuthoritativeSequence { get; set; }

        public ulong CanonicalStateHash { get; set; }

        public int RepairAttempts { get; set; }

        public HashSet<long> ReadyPlayers { get; set; } = [];

        public HashSet<long> RequiredPlayers { get; set; } = [];

        public bool BlocksSaving => Phase != CombatSynchronizationPhase.Stable;
    }
}
