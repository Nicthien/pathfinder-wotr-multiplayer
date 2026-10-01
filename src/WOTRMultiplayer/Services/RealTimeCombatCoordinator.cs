using System;
using System.Collections.Generic;
using System.Linq;
using WOTRMultiplayer.Entities.Combat.RealTime;

namespace WOTRMultiplayer.Services
{
    /// <summary>
    /// Host-side deterministic state machine for safe combat-mode changes and desync recovery.
    /// Game interaction and network transport deliberately live outside this class.
    /// </summary>
    public class RealTimeCombatCoordinator
    {
        private const int MaxRepairAttempts = 2;
        private readonly NetworkRealTimeCombatState _state;

        public RealTimeCombatCoordinator(NetworkRealTimeCombatState state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        public NetworkRealTimeCombatState State => _state;

        public long NextAuthoritativeSequence()
        {
            return ++_state.LastAuthoritativeSequence;
        }

        public long PublishAuthoritativeSnapshot(ulong canonicalStateHash)
        {
            if (_state.Mode != CombatMode.RealTimeWithPause || _state.Phase != CombatSynchronizationPhase.Stable)
            {
                throw new InvalidOperationException("Authoritative snapshots require stable real-time combat.");
            }

            _state.CanonicalStateHash = canonicalStateHash;
            return NextAuthoritativeSequence();
        }

        public bool TryCompareClientSnapshot(long epoch, long sequence, ulong expectedStateHash, ulong clientStateHash, out bool requiresRepair)
        {
            requiresRepair = false;
            if (_state.Mode != CombatMode.RealTimeWithPause || _state.Phase != CombatSynchronizationPhase.Stable
                || epoch != _state.Epoch || sequence > _state.LastAuthoritativeSequence || sequence <= 0)
            {
                return false;
            }

            requiresRepair = clientStateHash != expectedStateHash;
            return true;
        }

        public bool IsCurrent(long epoch, long sequence)
        {
            return _state.Mode == CombatMode.RealTimeWithPause
                && _state.Phase == CombatSynchronizationPhase.Stable
                && epoch == _state.Epoch && sequence > 0
                && sequence > _state.LastAuthoritativeSequence;
        }

        public void RequestModeSwitch(CombatMode requestedMode, IEnumerable<long> connectedPlayers)
        {
            if (_state.Phase != CombatSynchronizationPhase.Stable)
            {
                throw new InvalidOperationException("A synchronization barrier is already active.");
            }

            if (_state.Mode == requestedMode)
            {
                return;
            }

            var players = new HashSet<long>(connectedPlayers ?? Enumerable.Empty<long>());
            if (players.Count == 0)
            {
                throw new InvalidOperationException("A combat-mode switch requires at least one connected player.");
            }

            _state.PendingMode = requestedMode;
            _state.PreviousMode = _state.Mode;
            _state.Phase = CombatSynchronizationPhase.PreparingModeSwitch;
            _state.Epoch++;
            _state.LastAuthoritativeSequence = 0;
            _state.RequiredPlayers = players;
            _state.ReadyPlayers.Clear();
        }

        public bool AcknowledgeBarrier(long playerId, IEnumerable<long> connectedPlayers)
        {
            if (_state.Phase != CombatSynchronizationPhase.PreparingModeSwitch)
            {
                return false;
            }

            var currentPlayers = new HashSet<long>(connectedPlayers ?? Enumerable.Empty<long>());
            if (!_state.RequiredPlayers.SetEquals(currentPlayers) || !_state.RequiredPlayers.Contains(playerId))
            {
                return false;
            }

            _state.ReadyPlayers.Add(playerId);
            return _state.RequiredPlayers.IsSubsetOf(_state.ReadyPlayers);
        }

        public void CommitModeSwitch(ulong canonicalStateHash)
        {
            if (_state.Phase != CombatSynchronizationPhase.PreparingModeSwitch || !_state.PendingMode.HasValue)
            {
                throw new InvalidOperationException("No combat-mode switch is ready to commit.");
            }

            if (_state.RequiredPlayers.Count == 0 || !_state.RequiredPlayers.IsSubsetOf(_state.ReadyPlayers))
            {
                throw new InvalidOperationException("Every player must acknowledge the combat-mode switch.");
            }

            _state.Mode = _state.PendingMode.Value;
            _state.PendingMode = null;
            _state.CanonicalStateHash = canonicalStateHash;
            _state.RepairAttempts = 0;
            _state.ReadyPlayers.Clear();
            _state.Phase = CombatSynchronizationPhase.Synchronizing;
        }

        public bool AcknowledgeModeCommit(long playerId, IEnumerable<long> connectedPlayers)
        {
            if (_state.Phase != CombatSynchronizationPhase.Synchronizing)
            {
                return false;
            }

            var currentPlayers = new HashSet<long>(connectedPlayers ?? Enumerable.Empty<long>());
            if (!_state.RequiredPlayers.SetEquals(currentPlayers) || !_state.RequiredPlayers.Contains(playerId))
            {
                return false;
            }

            _state.ReadyPlayers.Add(playerId);
            return _state.RequiredPlayers.IsSubsetOf(_state.ReadyPlayers);
        }

        public void CompleteModeCommit()
        {
            if (_state.Phase != CombatSynchronizationPhase.Synchronizing
                || _state.RequiredPlayers.Count == 0
                || !_state.RequiredPlayers.IsSubsetOf(_state.ReadyPlayers))
            {
                throw new InvalidOperationException("Every player must apply the committed combat mode before resuming.");
            }

            _state.PreviousMode = null;
            _state.ReadyPlayers.Clear();
            _state.RequiredPlayers.Clear();
            _state.Phase = CombatSynchronizationPhase.Stable;
        }

        public void AbortModeSwitch()
        {
            if (_state.Phase != CombatSynchronizationPhase.PreparingModeSwitch
                && _state.Phase != CombatSynchronizationPhase.Synchronizing)
            {
                throw new InvalidOperationException("No combat-mode switch can be aborted.");
            }

            if (_state.PreviousMode.HasValue)
            {
                _state.Mode = _state.PreviousMode.Value;
            }
            _state.PendingMode = null;
            _state.PreviousMode = null;
            _state.ReadyPlayers.Clear();
            _state.RequiredPlayers.Clear();
            _state.Phase = CombatSynchronizationPhase.Stable;
        }

        public bool RequiresRepair(ulong clientStateHash)
        {
            return _state.Mode == CombatMode.RealTimeWithPause
                && _state.Phase == CombatSynchronizationPhase.Stable
                && clientStateHash != _state.CanonicalStateHash;
        }

        public void BeginRepair()
        {
            if (_state.Mode != CombatMode.RealTimeWithPause ||
                _state.Phase != CombatSynchronizationPhase.Stable && _state.Phase != CombatSynchronizationPhase.Repairing)
            {
                throw new InvalidOperationException("Cannot begin real-time repair outside a real-time repair barrier.");
            }

            _state.Phase = CombatSynchronizationPhase.Repairing;
            _state.RepairAttempts++;
            _state.Epoch++;
            _state.LastAuthoritativeSequence = 0;
            _state.ReadyPlayers.Clear();
        }

        public bool CompleteRepair(ulong repairedStateHash, ulong clientStateHash)
        {
            if (_state.Phase != CombatSynchronizationPhase.Repairing)
            {
                throw new InvalidOperationException("No repair is active.");
            }

            _state.CanonicalStateHash = repairedStateHash;
            if (repairedStateHash == clientStateHash)
            {
                _state.RepairAttempts = 0;
                _state.Phase = CombatSynchronizationPhase.Stable;
                return true;
            }

            if (_state.RepairAttempts >= MaxRepairAttempts)
            {
                _state.PendingMode = CombatMode.TurnBased;
                _state.Phase = CombatSynchronizationPhase.FallingBackToTurnBased;
            }
            return false;
        }

        public void CommitFallback(ulong canonicalStateHash)
        {
            if (_state.Phase != CombatSynchronizationPhase.FallingBackToTurnBased)
            {
                throw new InvalidOperationException("No turn-based fallback is active.");
            }

            _state.Mode = CombatMode.TurnBased;
            _state.PendingMode = null;
            _state.CanonicalStateHash = canonicalStateHash;
            _state.RepairAttempts = 0;
            _state.ReadyPlayers.Clear();
            _state.Phase = CombatSynchronizationPhase.Stable;
        }

    }
}
