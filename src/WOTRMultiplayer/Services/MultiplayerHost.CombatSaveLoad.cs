using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using ProtoBuf;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerHost
    {
        // A peer may legitimately remain inside Owlcat's native area loader for
        // close to two minutes (the native loader has its own two-minute
        // deadline).  The restore barrier starts as soon as the host area is
        // loaded, so the ordinary one-minute network timeout can expire before
        // a slower peer is even able to inspect RestorePrepare.
        private static readonly TimeSpan CombatRestoreLoadTimeout = TimeSpan.FromMinutes(3);

        private NotifyCombatSave _combatRestore;
        private readonly HashSet<long> _combatRestorePeers = new();
        private readonly HashSet<long> _combatRestoreAcknowledged = new();
        private DateTime _combatRestoreDeadline, _combatRestoreNext, _combatRestoreReadinessLogNext;

        private bool BeginCombatSaveRestoreBarrier()
        {
            if (PendingCombatSave == null) return false;
            try
            {
                RestoreCombatSaveAfterAreaLoaded();
                _combatRestorePeers.Clear(); _combatRestoreAcknowledged.Clear();
                _combatRestorePeers.UnionWith(Game.Players.Where(p => p.Id != Game.LocalPlayerId).Select(p => p.Id));
                _combatRestore = new NotifyCombatSave { SessionId = Game.Id, SessionSeed = Game.SessionSeed,
                    LoadedSaveSeed = Game.LoadedSaveSeed, Epoch = Game.RealTimeCombat.Epoch,
                    Operation = Guid.NewGuid().ToString("N"), Stage = "RestorePrepare" };
                _combatRestoreDeadline = DateTime.UtcNow.Add(CombatRestoreLoadTimeout); _combatRestoreNext = DateTime.MinValue;
                _combatRestoreReadinessLogNext = DateTime.MinValue;
                TickCombatSaveRestore();
            }
            catch (Exception error) { FailCombatSaveRestore(error.Message); }
            return true;
        }

        internal void TickCombatSaveRestore()
        {
            if (_combatRestore == null) return;
            if (!IsCurrentCombatSave(_combatRestore))
            {
                // A superseded load cannot pause or fail its replacement world.
                _combatRestore = null; _combatRestorePeers.Clear(); _combatRestoreAcknowledged.Clear();
                return;
            }
            if (!IsActive || DateTime.UtcNow > _combatRestoreDeadline
                || !_combatRestorePeers.SetEquals(Game.Players.Where(p => p.Id != Game.LocalPlayerId).Select(p => p.Id)))
            { FailCombatSaveRestore("timeout-or-session-changed"); return; }
            if (TryFinalizeCombatSaveLoadedState()) return;
            // OnAreaLoaded rebuilds the native turn before the engine has
            // completed its loading process. Do not send/advance restoration
            // receipts until OnGameLoaded makes topology capture legal.
            if (!IsCombatSaveRestoreReady)
            {
                if (DateTime.UtcNow >= _combatRestoreReadinessLogNext)
                {
                    _combatRestoreReadinessLogNext = DateTime.UtcNow.AddSeconds(2);
                    Logger.LogInformation("[CombatSave] Waiting for restored native turn to become stable. State={State}",
                        CombatSaveRestoreReadiness);
                }
                return;
            }
            if (_combatRestoreAcknowledged.SetEquals(_combatRestorePeers))
            {
                if (_combatRestore.Stage == "RestorePrepare")
                {
                    _combatRestore.Stage = "RestoreCommit";
                    _combatRestoreAcknowledged.Clear(); _combatRestoreNext = DateTime.MinValue;
                }
                else if (_combatRestore.Stage == "RestoreCommit")
                {
                    _combatRestore.Stage = "RestoreComplete";
                    _combatRestoreAcknowledged.Clear(); _combatRestoreNext = DateTime.MinValue;
                }
                else if (_combatRestore.Stage == "RestoreComplete")
                {
                    try
                    {
                        VerifyCombatSaveRestore();
                        var release = Serializer.DeepClone(_combatRestore); release.Stage = "RestoreRelease";
                        // Transport is reliable and ordered. Queue release before
                        // any resumed host commands can be sent on this connection.
                        foreach (var peer in _combatRestorePeers) Send(peer, release);
                        CompleteCombatSaveRestore();
                    }
                    catch (Exception error) { FailCombatSaveRestore(error.Message); return; }
                    _combatRestore = null;
                    return;
                }
            }
            if (DateTime.UtcNow < _combatRestoreNext) return;
            _combatRestoreNext = DateTime.UtcNow.AddSeconds(2);
            foreach (var peer in _combatRestorePeers) Send(peer, _combatRestore);
        }

        private void AcceptCombatRestoreAck(long peer, ClientCombatSaveAck frame)
        {
            if (_combatRestore == null || !_combatRestorePeers.Contains(peer)
                || !IsCurrentCombatSave(frame.Identity) || frame.Identity.Operation != _combatRestore.Operation
                || frame.Identity.Stage != _combatRestore.Stage) return;
            if (frame.Refusal != null || frame.Digest != PendingCombatSave.RestoreDigest)
            { FailCombatSaveRestore("peer-restoration-divergent"); return; }
            _combatRestoreAcknowledged.Add(peer);
            Logger.LogInformation("[CombatSave] Restore peer confirmed. Peer={Peer}, Stage={Stage}", peer, frame.Identity.Stage);
            TickCombatSaveRestore();
        }

        private void FailCombatSaveRestore(string reason)
        {
            _turnEndSynchronizationFailed = true;
            GameInteraction.SetPause(true);
            if (_combatRestore != null)
            {
                var failure = Serializer.DeepClone(_combatRestore); failure.Stage = "RestoreFailed"; failure.Result = reason;
                foreach (var peer in _combatRestorePeers) if (GetPlayer(peer) != null) Send(peer, failure);
            }
            _combatRestore = null;
            Logger.LogError("[CombatSave] Restoration stopped. Reason={Reason}. Load a safe save; do not restart this turn.", reason);
            PlayerNotification.ShowModalMessage(WellKnownKeys.LobbyWindow.RealTime.SaveCombatLoadFailed.Key);
        }
    }
}
