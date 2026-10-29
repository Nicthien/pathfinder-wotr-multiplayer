using System;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerHost
    {
        private DateTime _nextTurnStartRecovery;
        private NotifyInvalidCombatTurnStarted TurnRecoveryMessage(string unitId) => new()
        {
            UnitId = unitId, SessionId = Game.Id, LoadedSaveSeed = Game.LoadedSaveSeed,
            CombatSeed = Game.Combat.Seed, Round = Game.Combat.Round
        };

        private void TickTurnStartRecovery()
        {
            if (!IsActive || Game?.Combat?.Stage != NetworkCombatStage.Running
                || Game.RealTimeCombat.Mode != CombatMode.TurnBased
                || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable
                || Game.Combat.Turn?.Stage != NetworkCombatTurnStage.Starting
                || DateTime.UtcNow < _nextTurnStartRecovery) return;
            _nextTurnStartRecovery = DateTime.UtcNow.AddSeconds(2);
            lock (ActionLock)
            {
                var missing = GetMissingPlayers(Game.Combat.Turn.UnitId, Game.Combat.PlayersNextTurnInitialization);
                foreach (var player in missing)
                {
                    if (player.IsHost) continue;
                    Logger.LogWarning("[TurnRecovery] Requesting missing turn initialization. Player={Player}, Unit={Unit}, Round={Round}",
                        player.Id, Game.Combat.Turn.UnitId, Game.Combat.Round);
                    Send(player.Id, TurnRecoveryMessage(Game.Combat.Turn.UnitId));
                }
                // Also resolves a pending barrier after a peer disconnected.
                if (missing.Count == 0) TryStartTurn();
            }
        }
    }
}
