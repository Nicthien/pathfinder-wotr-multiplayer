using System.Collections.Generic;
using WOTRMultiplayer.Entities.Combat.RealTime;

namespace WOTRMultiplayer.Services
{
    public enum RealTimeCommandRejection
    {
        None = 0,
        RealTimeModeInactive,
        SynchronizationBarrierActive,
        WrongEpoch,
        InvalidSequence,
        SenderMismatch,
        WrongSession,
        WrongLoadedSave,
        InvalidCommandId,
        MalformedCommand,
        GameExecutionRejected,
        StateChangedDuringExecution,
        UnknownUnit,
        UnitOwnedByAnotherPlayer,
    }

    public class RealTimeCommandGate
    {
        private readonly NetworkRealTimeCombatState _state;
        private readonly Dictionary<long, long> _lastSequenceByPlayer = [];
        private long _trackedEpoch;
        private readonly string _sessionId;
        private readonly int _loadedSaveSeed;

        public RealTimeCommandGate(NetworkRealTimeCombatState state, string sessionId = null, int loadedSaveSeed = 0)
        {
            _state = state ?? throw new System.ArgumentNullException(nameof(state));
            _trackedEpoch = state.Epoch;
            _sessionId = sessionId;
            _loadedSaveSeed = loadedSaveSeed;
        }

        public RealTimeCommandRejection Validate(
            RealTimeCommandEnvelope command,
            long receivedFrom,
            IReadOnlyDictionary<string, long> unitOwners)
        {
            if (command == null || unitOwners == null || command.Sequence <= 0)
            {
                return RealTimeCommandRejection.InvalidSequence;
            }

            if (_state.Mode != CombatMode.RealTimeWithPause)
            {
                return RealTimeCommandRejection.RealTimeModeInactive;
            }

            if (_state.Phase != CombatSynchronizationPhase.Stable)
            {
                return RealTimeCommandRejection.SynchronizationBarrierActive;
            }

            if (command.Epoch != _state.Epoch)
            {
                return RealTimeCommandRejection.WrongEpoch;
            }

            if (command.PlayerId != receivedFrom)
            {
                return RealTimeCommandRejection.SenderMismatch;
            }

            if (_sessionId != null && !string.Equals(command.SessionId, _sessionId, System.StringComparison.Ordinal))
            {
                return RealTimeCommandRejection.WrongSession;
            }

            if (_sessionId != null && command.LoadedSaveSeed != _loadedSaveSeed)
            {
                return RealTimeCommandRejection.WrongLoadedSave;
            }

            if (_sessionId != null && !string.Equals(command.CommandId,
                    $"{command.PlayerId}:{command.Epoch}:{command.Sequence}", System.StringComparison.Ordinal))
            {
                return RealTimeCommandRejection.InvalidCommandId;
            }

            if (_trackedEpoch != _state.Epoch)
            {
                _lastSequenceByPlayer.Clear();
                _trackedEpoch = _state.Epoch;
            }

            if (_lastSequenceByPlayer.TryGetValue(command.PlayerId, out var lastSequence)
                && command.Sequence <= lastSequence)
            {
                return RealTimeCommandRejection.InvalidSequence;
            }

            if (string.IsNullOrWhiteSpace(command.UnitId) || !unitOwners.TryGetValue(command.UnitId, out var ownerId))
            {
                return RealTimeCommandRejection.UnknownUnit;
            }

            if (ownerId != command.PlayerId)
            {
                return RealTimeCommandRejection.UnitOwnedByAnotherPlayer;
            }

            return RealTimeCommandRejection.None;
        }

        public bool TryCommit(RealTimeCommandEnvelope command)
        {
            if (command == null || command.Epoch != _state.Epoch || command.Sequence <= 0)
            {
                return false;
            }

            if (_trackedEpoch != _state.Epoch)
            {
                _lastSequenceByPlayer.Clear();
                _trackedEpoch = _state.Epoch;
            }

            if (_lastSequenceByPlayer.TryGetValue(command.PlayerId, out var lastSequence)
                && command.Sequence <= lastSequence)
            {
                return false;
            }

            _lastSequenceByPlayer[command.PlayerId] = command.Sequence;
            return true;
        }

        public void Reset()
        {
            _lastSequenceByPlayer.Clear();
            _trackedEpoch = _state.Epoch;
        }
    }
}
