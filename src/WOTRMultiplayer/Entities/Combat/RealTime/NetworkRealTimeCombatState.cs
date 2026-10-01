using System.Collections.Generic;

namespace WOTRMultiplayer.Entities.Combat.RealTime
{
    public class NetworkRealTimeCombatState
    {
        public const int ProtocolVersion = 26;

        public List<RealTimeCommandEnvelope> AcceptedOrders { get; set; } = [];

        public CombatMode Mode { get; set; } = CombatMode.TurnBased;

        public CombatMode PreferredMode { get; set; } = CombatMode.TurnBased;

        public long PreferenceSequence { get; set; }

        public int ResponseTimeMs { get; set; }

        public long MissingSnapshotReplies { get; set; }

        public CombatMode? PendingMode { get; set; }

        public CombatMode? PreviousMode { get; set; }

        public PausePolicy PausePolicy { get; set; } = PausePolicy.AnyoneConsensusResume;

        public CombatSynchronizationPhase Phase { get; set; } = CombatSynchronizationPhase.Stable;

        public long Epoch { get; set; }

        public long LastAuthoritativeSequence { get; set; }

        public ulong CanonicalStateHash { get; set; }

        public int RepairAttempts { get; set; }

        public HashSet<long> ReadyPlayers { get; set; } = [];

        public HashSet<long> RequiredPlayers { get; set; } = [];

        public HashSet<long> PausedPlayers { get; set; } = [];

        public HashSet<long> ResumeVotes { get; set; } = [];

        public long PauseSequence { get; set; }

        public bool IsManualPauseActive => PausedPlayers.Count > 0;

        public bool BlocksSaving => Phase != CombatSynchronizationPhase.Stable;
    }
}
