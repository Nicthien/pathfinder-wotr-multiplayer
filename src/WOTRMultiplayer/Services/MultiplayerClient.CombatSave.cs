using System;
using Microsoft.Extensions.Logging;
using ProtoBuf;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerClient
    {
        private NotifyCombatSave _combatSaveHeader;
        private ClientCombatSaveAck _combatSaveAck;
        private CombatSavePauseLease _combatSavePause;
        private NetworkGame _combatSaveSession;
        private DateTime _combatSaveDeadline;
        private readonly System.Collections.Generic.HashSet<string> _completedCombatSaves = new();
        private NotifyCombatSave _combatRestoreHeader;
        private bool _combatRestoreCommitted;
        private ClientCombatSaveAck _combatRestoreCompletionAck;
        private string _combatRestoreFailedOperation;
        private NetworkGame _combatSaveReplaySession;
        private int _combatSaveReplayLoad;
        private long _combatSaveReplayEpoch;

        private void OnCombatSave(long peer, NotifyCombatSave message)
        {
            if (peer != NetworkConstants.HostPlayerId || !CombatSaveActivation.Enabled || message == null
                || message.Operation?.Length != 32 || message.SessionId?.Length > 128 || message.Result?.Length > 128
                || message.Stage != "Prepare" && message.Stage != "Finish" && message.Stage != "RestorePrepare"
                    && message.Stage != "RestoreCommit" && message.Stage != "RestoreComplete"
                    && message.Stage != "RestoreRelease" && message.Stage != "RestoreFailed") return;
            var frame = Serializer.DeepClone(message);
            _mainThreadAccessor.Post(() =>
            {
                if (!IsActive || !IsCurrentCombatSave(frame)) return;
                if (!ReferenceEquals(_combatSaveReplaySession, Game) || _combatSaveReplayLoad != Game.LoadedSaveSeed
                    || _combatSaveReplayEpoch != Game.RealTimeCombat.Epoch)
                {
                    _completedCombatSaves.Clear();
                    _combatRestoreFailedOperation = null;
                    _combatRestoreCompletionAck = null;
                    _combatSaveReplaySession = Game; _combatSaveReplayLoad = Game.LoadedSaveSeed;
                    _combatSaveReplayEpoch = Game.RealTimeCombat.Epoch;
                }
                if (frame.Stage.StartsWith("Restore", StringComparison.Ordinal))
                { HandleCombatSaveRestore(frame); return; }
                if (frame.Stage == "Finish")
                {
                    _completedCombatSaves.Add(frame.Operation); // Reject a delayed Prepare even if Finish arrived first.
                    if (_combatSaveHeader?.Operation != frame.Operation) return;
                    // A disk/publication failure does not corrupt a verified world.
                    // The host explicitly marks unsafe capture failures as divergence.
                    if (frame.Result == "divergence")
                    {
                        _combatSavePause?.Protect(); _turnEndSynchronizationFailed = true;
                        CombatSaveLoadState.Owner = this;
                        CombatSaveLoadState.BindLoadedWorld(Kingmaker.Game.Instance.State);
                    }
                    EndCombatSave();
                    PlayerNotification.ShowWarningNotification(CombatSaveMessages.ForResult(frame.Result), addToLog: false);
                    Logger.LogInformation("[CombatSave] Host completed operation. Operation={Operation}, Result={Result}", frame.Operation, frame.Result);
                    return;
                }
                if (_completedCombatSaves.Contains(frame.Operation)) return;
                if (_combatSaveHeader != null)
                {
                    if (_combatSaveHeader.Operation == frame.Operation && _combatSaveAck != null) Send(_combatSaveAck);
                    return;
                }
                var refusal = IsSavingBlockedByCombatSync ? "synchronization-required" : CombatSaveRefusal();
                if (refusal != null) { Send(new ClientCombatSaveAck { Identity = frame, Refusal = refusal }); return; }
                try
                {
                    _combatSaveSession = Game; _combatSaveHeader = frame;
                    _combatSavePause = new CombatSavePauseLease(() => IsActive && Game == _combatSaveSession
                        && Game.ForcedPause == null && !IsSavingBlockedByCombatSync);
                    _combatSaveDeadline = DateTime.UtcNow.AddSeconds(60);
                    _combatSaveAck = new ClientCombatSaveAck { Identity = frame, Digest = CaptureCombatSaveDigest() };
                    Send(_combatSaveAck);
                    PlayerNotification.ShowWarningNotification(WellKnownKeys.LobbyWindow.RealTime.SaveCombatWorking.Key, addToLog: false);
                    Logger.LogInformation("[CombatSave] Client paused and verified local state. Operation={Operation}", frame.Operation);
                }
                catch (Exception error)
                {
                    Logger.LogError(error, "[CombatSave] Client preparation failed");
                    Send(new ClientCombatSaveAck { Identity = frame, Refusal = "capture-failed" });
                    EndCombatSave();
                }
            });
        }

        private void EndCombatSave()
        {
            _combatSavePause?.Dispose(); _combatSavePause = null;
            _combatSaveHeader = null; _combatSaveAck = null; _combatSaveSession = null;
        }

        private void ResetCombatSaveState()
        {
            // Disconnect is not confirmation of a save/load outcome. Release the
            // command lease without unpausing the old world; clear replay state.
            _combatSavePause?.Protect();
            EndCombatSave();
            _combatRestoreHeader = null; _combatRestoreCommitted = false;
            _combatRestoreCompletionAck = null; _combatRestoreFailedOperation = null;
            _completedCombatSaves.Clear(); _combatSaveReplaySession = null;
        }

        private void HandleCombatSaveRestore(NotifyCombatSave frame)
        {
            if (frame.Operation == _combatRestoreFailedOperation) return;
            if (frame.Stage != "RestoreFailed" && TryFinalizeCombatSaveLoadedState()) return;
            // The host retries every restoration stage. Ignore an early frame
            // while this peer is between OnAreaLoaded and OnGameLoaded rather
            // than attempting an unsafe native topology capture.
            if (frame.Stage != "RestoreFailed" && !IsCombatSaveRestoreReady) return;
            // A receipt is not permission to tick: keep the world held until the
            // host has collected every receipt and sends its ordered release.
            if (frame.Stage == "RestoreComplete" && _combatRestoreCompletionAck?.Identity.Operation == frame.Operation)
            { Send(_combatRestoreCompletionAck); return; }
            if (PendingCombatSave == null || _combatRestoreHeader != null && _combatRestoreHeader.Operation != frame.Operation) return;
            try
            {
                if (frame.Stage == "RestoreFailed") throw new InvalidOperationException("host-restore-failed");
                if (frame.Stage == "RestorePrepare")
                {
                    if (_turnEndSynchronizationFailed) throw new InvalidOperationException("client-restore-failed");
                    if (_combatRestoreHeader == null)
                    { _combatRestoreHeader = frame; _combatRestoreCommitted = false; _combatSaveDeadline = DateTime.UtcNow.AddSeconds(60); }
                    var digest = CaptureCombatSaveRestoreDigest();
                    Send(new ClientCombatSaveAck { Identity = frame, Digest = digest });
                }
                else if (frame.Stage == "RestoreCommit" && _combatRestoreHeader != null)
                {
                    var digest = CaptureCombatSaveRestoreDigest();
                    if (digest != PendingCombatSave.RestoreDigest) throw new InvalidOperationException("client-restore-changed");
                    _combatRestoreCommitted = true;
                    Send(new ClientCombatSaveAck { Identity = frame, Digest = digest });
                }
                else if (frame.Stage == "RestoreComplete" && _combatRestoreCommitted)
                {
                    var digest = CaptureCombatSaveRestoreDigest();
                    if (digest != PendingCombatSave.RestoreDigest) throw new InvalidOperationException("client-restore-changed");
                    _combatRestoreCompletionAck = new ClientCombatSaveAck { Identity = frame, Digest = digest };
                    Send(_combatRestoreCompletionAck);
                }
                else if (frame.Stage == "RestoreRelease" && _combatRestoreCommitted
                    && _combatRestoreCompletionAck?.Identity.Operation == frame.Operation)
                {
                    CompleteCombatSaveRestore(); _combatRestoreHeader = null; _combatRestoreCommitted = false;
                }
            }
            catch (Exception error)
            {
                _combatRestoreFailedOperation = frame.Operation;
                _turnEndSynchronizationFailed = true;
                GameInteraction.SetPause(true);
                Send(new ClientCombatSaveAck { Identity = frame, Refusal = "restoration-failed" });
                Logger.LogError(error, "[CombatSave] Client restoration stopped; load a safe save.");
                PlayerNotification.ShowModalMessage(WellKnownKeys.LobbyWindow.RealTime.SaveCombatLoadFailed.Key);
                _combatRestoreHeader = null;
                _combatRestoreCommitted = false;
            }
        }

        internal void TickCombatSave()
        {
            if (_combatRestoreHeader != null && !IsCurrentCombatSave(_combatRestoreHeader))
            { _combatRestoreHeader = null; _combatRestoreCommitted = false; _combatRestoreCompletionAck = null; }
            if (_combatRestoreHeader != null && (!IsActive
                || DateTime.UtcNow > _combatSaveDeadline))
            {
                _combatRestoreFailedOperation = _combatRestoreHeader.Operation;
                _turnEndSynchronizationFailed = true; _combatRestoreHeader = null; _combatRestoreCommitted = false;
                GameInteraction.SetPause(true);
                PlayerNotification.ShowModalMessage(WellKnownKeys.LobbyWindow.RealTime.SaveCombatLoadFailed.Key);
            }
            if (_combatSaveHeader == null) return;
            if (Game != _combatSaveSession || !IsCurrentCombatSave(_combatSaveHeader))
            { _combatSavePause?.Protect(); EndCombatSave(); return; }
            if (!IsActive
                || DateTime.UtcNow > _combatSaveDeadline)
            {
                // An unknown outcome is not permission to resume. Preserve a safety pause.
                _combatSavePause?.Protect();
                _turnEndSynchronizationFailed = true;
                _completedCombatSaves.Add(_combatSaveHeader.Operation);
                Logger.LogError("[CombatSave] Client lost the save barrier; safety pause retained. Operation={Operation}", _combatSaveHeader.Operation);
                EndCombatSave();
            }
        }
    }
}
