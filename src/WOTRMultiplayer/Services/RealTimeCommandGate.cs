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
        UnknownUnit,
        UnitOwnedByAnotherPlayer,
    }

    public class RealTimeCommandGate
    {
        private readonly NetworkRealTimeCombatState _state;
        private readonly Dictionary<long, long> _lastSequenceByPlayer = [];

        public RealTimeCommandGate(NetworkRealTimeCombatState state)
        {
            _state = state;
        }

        public RealTimeCommandRejection Validate(
            RealTimeCommandEnvelope command,
            IReadOnlyDictionary<string, long> unitOwners)
        {
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

            _lastSequenceByPlayer[command.PlayerId] = command.Sequence;
            return RealTimeCommandRejection.None;
        }

        public void Reset()
        {
            _lastSequenceByPlayer.Clear();
        }
    }
}
