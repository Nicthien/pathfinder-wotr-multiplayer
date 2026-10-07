using System;
using System.Collections.Generic;
using System.Linq;
using WOTRMultiplayer.Entities.Combat.RealTime;

namespace WOTRMultiplayer.Services
{
    public class RealTimePauseCoordinator
    {
        private readonly NetworkRealTimeCombatState _state;

        public RealTimePauseCoordinator(NetworkRealTimeCombatState state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        public bool TryRequest(long playerId, long hostPlayerId, bool wantsPause, IEnumerable<long> connectedPlayers)
        {
            var players = new HashSet<long>(connectedPlayers ?? Enumerable.Empty<long>());
            if (_state.CombatEndPending || _state.Mode != CombatMode.RealTimeWithPause || _state.Phase != CombatSynchronizationPhase.Stable
                || !players.Contains(playerId) || !players.Contains(hostPlayerId))
            {
                return false;
            }

            if (wantsPause)
            {
                if (_state.PausePolicy == PausePolicy.HostOnly && playerId != hostPlayerId)
                {
                    return false;
                }

                var added = _state.PausedPlayers.Add(playerId);
                if (added)
                {
                    _state.ResumeVotes.Clear();
                    _state.PauseSequence++;
                }
                return added;
            }

            if (!_state.IsManualPauseActive)
            {
                return false;
            }

            switch (_state.PausePolicy)
            {
                case PausePolicy.HostOnly when playerId == hostPlayerId:
                case PausePolicy.AnyoneFreeResume:
                    ClearPause();
                    return true;
                case PausePolicy.AnyoneConsensusResume:
                    if (!_state.ResumeVotes.Add(playerId))
                    {
                        return false;
                    }
                    if (players.IsSubsetOf(_state.ResumeVotes))
                    {
                        ClearPause();
                    }
                    else
                    {
                        _state.PauseSequence++;
                    }
                    return true;
                default:
                    return false;
            }
        }

        private void ClearPause()
        {
            _state.PausedPlayers.Clear();
            _state.ResumeVotes.Clear();
            _state.PauseSequence++;
        }
    }
}
