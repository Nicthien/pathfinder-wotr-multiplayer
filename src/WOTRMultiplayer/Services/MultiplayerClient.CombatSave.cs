using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProtoBuf;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerClient
    {
        private NotifyCombatSave _combatSaveHeader;
        private NotifyCombatSave _combatSaveAlignment;
        private ClientCombatSaveAck _combatSaveAlignmentAck;
        private bool _combatSaveAlignmentRecoveryRequested;
        private bool _combatSaveAlignmentHydrating;
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
                || message.UnitId?.Length > 128 || message.NativeTurnState?.Length > 16 * 1024 * 1024
                || message.Stage != "Align" && message.Stage != "Prepare" && message.Stage != "Finish" && message.Stage != "RestorePrepare"
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
                if (frame.Stage == "Align")
                { HandleCombatSaveAlignment(frame); return; }
                if (frame.Stage == "Finish")
                {
                    _completedCombatSaves.Add(frame.Operation); // Reject a delayed Prepare even if Finish arrived first.
                    if (_combatSaveHeader?.Operation != frame.Operation
                        && _combatSaveAlignment?.Operation != frame.Operation) return;
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
                if (_combatSaveAlignment?.Operation == frame.Operation)
                {
                    if (_combatSaveAlignmentAck == null) return;
                    _combatSaveAlignment = null; _combatSaveAlignmentAck = null;
                    _combatSaveAlignmentRecoveryRequested = false;
                    _combatSaveAlignmentHydrating = false;
                }
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
                    _combatSavePause ??= new CombatSavePauseLease(() => IsActive && Game == _combatSaveSession
                        && Game.ForcedPause == null && !IsSavingBlockedByCombatSync);
                    _combatSaveDeadline = DateTime.UtcNow.AddSeconds(60);
                    // A paused Owlcat frame can still run late combat callbacks
                    // between the Align receipt and this Prepare receipt.  They
                    // can recreate one engagement or reset an AI timestamp even
                    // though no game time elapsed.  Seal the exact host native
                    // supplement again in the same main-thread callback that
                    // computes the digest.
                    if (CombatSaveDigestReader == null)
                    {
                        if (string.IsNullOrWhiteSpace(frame.NativeTurnState))
                            throw new InvalidOperationException("combat-save-prepare-native-state-missing");
                        TurnBasedSaveNativeRestore.RestoreSupplement(frame.NativeTurnState);
                    }
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

        private void HandleCombatSaveAlignment(NotifyCombatSave frame)
        {
            if (_completedCombatSaves.Contains(frame.Operation)) return;
            if (_combatSaveAlignment?.Operation == frame.Operation)
            {
                if (_combatSaveAlignmentAck != null) Send(_combatSaveAlignmentAck);
                else RequestCombatSaveTurnAlignment();
                return;
            }
            if (_combatSaveHeader != null || _combatSaveAlignment != null)
            { Send(new ClientCombatSaveAck { Identity = frame, Refusal = "busy" }); return; }
            if (Game?.Combat?.Stage != Entities.Combat.NetworkCombatStage.Running
                || Game.RealTimeCombat?.Mode != Entities.Combat.RealTime.CombatMode.TurnBased
                || Game.RealTimeCombat.Phase != Entities.Combat.RealTime.CombatSynchronizationPhase.Stable
                || IsSavingBlockedByCombatSync || frame.Round != Game.Combat.Round
                || string.IsNullOrWhiteSpace(frame.UnitId))
            { Send(new ClientCombatSaveAck { Identity = frame, Refusal = "turn-alignment-required" }); return; }
            _combatSaveAlignment = frame; _combatSaveAlignmentAck = null;
            _combatSaveAlignmentRecoveryRequested = false;
            _combatSaveAlignmentHydrating = false;
            _combatSaveDeadline = DateTime.UtcNow.AddSeconds(60);
            RequestCombatSaveTurnAlignment();
        }

        private void RequestCombatSaveTurnAlignment()
        {
            var frame = _combatSaveAlignment;
            if (frame == null || !IsCurrentCombatSave(frame)) return;
            if (TryAcknowledgeCombatSaveTurnAlignment()) return;
            if (_combatSaveAlignmentRecoveryRequested) return;
            _combatSaveAlignmentRecoveryRequested = true;
            Logger.LogWarning("[CombatSave] Aligning client to host turn. Operation={Operation}, Round={Round}, Unit={Unit}",
                frame.Operation, frame.Round, frame.UnitId);
            OnNotifyInvalidCombatTurnStarted(NetworkConstants.HostPlayerId,
                new NotifyInvalidCombatTurnStarted
                {
                    SessionId = frame.SessionId, LoadedSaveSeed = frame.LoadedSaveSeed,
                    CombatSeed = Game.Combat.Seed, Round = frame.Round, UnitId = frame.UnitId
                });
        }

        private bool TryAcknowledgeCombatSaveTurnAlignment()
        {
            var frame = _combatSaveAlignment; var turn = Game?.Combat?.Turn;
            if (frame == null || turn == null || Game.Combat.Round != frame.Round
                || turn.Stage != Entities.Combat.NetworkCombatTurnStage.Playing || turn.IsAI
                || turn.Seed != frame.TurnSeed
                || !string.Equals(turn.UnitId, frame.UnitId, StringComparison.OrdinalIgnoreCase)
                || Kingmaker.Game.Instance?.TurnBasedCombatController?.CurrentTurn?.Rider?.UniqueId != frame.UnitId)
                return false;
            var refusal = IsSavingBlockedByCombatSync ? "synchronization-required" : CombatSaveRefusal();
            if (refusal != null)
            {
                Send(new ClientCombatSaveAck { Identity = frame, Refusal = refusal });
                _combatSaveAlignment = null; _combatSaveAlignmentRecoveryRequested = false;
                return false;
            }
            if (_combatSaveAlignmentAck != null) return true;
            if (_combatSaveAlignmentHydrating) return true;
            _combatSaveSession = Game;
            _combatSavePause = new CombatSavePauseLease(() => IsActive && Game == _combatSaveSession
                && Game.ForcedPause == null && !IsSavingBlockedByCombatSync);
            _combatSaveAlignmentHydrating = true;
            _ = HydrateCombatSaveAlignmentAsync(frame, Game);
            return true;
        }

        private async Task HydrateCombatSaveAlignmentAsync(NotifyCombatSave frame, NetworkGame session)
        {
            bool Current() => IsActive && ReferenceEquals(Game, session)
                && _combatSaveAlignment?.Operation == frame.Operation && IsCurrentCombatSave(frame);
            try
            {
                // Protocol tests may inject a digest reader without a Unity world.
                // Production always requires both authoritative payloads.
                if (CombatSaveDigestReader == null)
                {
                    if (frame.State == null || string.IsNullOrWhiteSpace(frame.NativeTurnState))
                        throw new InvalidOperationException("combat-save-alignment-state-missing");
                    var expected = Mapper.Map<NetworkCombatState>(frame.State);
                    var actual = await CombatInteraction.TryApplyAndReadRealTimeCombatStateAsync(expected, Current);
                    if (actual == null || !Current()) return;
                    var temporal = RealTimeCombatTemporalDiff.Summarize(expected, actual);
                    var expectedHash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = expected });
                    var actualHash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = actual });
                    if (temporal != null || expectedHash != actualHash)
                        throw new InvalidOperationException("combat-save-alignment-world-differs:"
                            + temporal + ";" + RealTimeCombatDiff.Summarize(expected, actual));
                    if (!await CombatSaveOnMainThreadAsync(() =>
                    {
                        if (!Current()) return false;
                        TurnBasedSaveNativeRestore.Restore(frame.NativeTurnState, frame.UnitId, frame.Round);
                        return Current();
                    })) return;
                    // Restoring the native turn also restores its complete unit
                    // relation supplement. Reassert the host world afterwards so
                    // a stale relation to a non-participant cannot survive between
                    // Align and Prepare and manufacture a false save divergence.
                    actual = await CombatInteraction.TryApplyAndReadRealTimeCombatStateAsync(expected, Current);
                    if (actual == null || !Current()) return;
                    temporal = RealTimeCombatTemporalDiff.Summarize(expected, actual);
                    actualHash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = actual });
                    if (temporal != null || expectedHash != actualHash)
                        throw new InvalidOperationException("combat-save-post-turn-alignment-world-differs:"
                            + temporal + ";" + RealTimeCombatDiff.Summarize(expected, actual));
                    // Applying the network checkpoint only owns the active combat
                    // projection.  The native supplement also contains relations
                    // and AI timers for loaded non-participants.  Reapply it last:
                    // otherwise the engine can retain locally generated engagement
                    // edges outside that projection and the following Prepare digest
                    // differs even though the visible combat is synchronized.
                    actual = await CombatSaveOnMainThreadAsync(() =>
                    {
                        if (!Current()) return null;
                        TurnBasedSaveNativeRestore.RestoreSupplement(frame.NativeTurnState);
                        return CombatInteraction.GetRealTimeCombatState();
                    });
                    if (actual == null || !Current()) return;
                    temporal = RealTimeCombatTemporalDiff.Summarize(expected, actual);
                    actualHash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = actual });
                    if (temporal != null || expectedHash != actualHash)
                        throw new InvalidOperationException("combat-save-post-supplement-alignment-world-differs:"
                            + temporal + ";" + RealTimeCombatDiff.Summarize(expected, actual));
                }
                await CombatSaveOnMainThreadAsync(() =>
                {
                    if (!Current()) return false;
                    _combatSaveAlignmentAck = new ClientCombatSaveAck { Identity = frame };
                    Send(_combatSaveAlignmentAck);
                    Logger.LogInformation("[CombatSave] Client hydrated and aligned to host turn. Operation={Operation}, Round={Round}, Unit={Unit}",
                        frame.Operation, frame.Round, frame.UnitId);
                    return true;
                });
            }
            catch (Exception error)
            {
                await CombatSaveOnMainThreadAsync(() =>
                {
                    if (!Current()) return false;
                    _combatSaveAlignmentAck = new ClientCombatSaveAck
                    { Identity = frame, Refusal = "alignment-restore-failed" };
                    Send(_combatSaveAlignmentAck);
                    Logger.LogError(error, "[CombatSave] Client authoritative alignment failed");
                    return true;
                });
            }
        }

        private Task<T> CombatSaveOnMainThreadAsync<T>(Func<T> action)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainThreadAccessor.Post(() =>
            {
                try { completion.TrySetResult(action()); }
                catch (Exception error) { completion.TrySetException(error); }
            });
            return completion.Task;
        }

        private void EndCombatSave()
        {
            _combatSavePause?.Dispose(); _combatSavePause = null;
            _combatSaveHeader = null; _combatSaveAck = null; _combatSaveSession = null;
            _combatSaveAlignment = null; _combatSaveAlignmentAck = null;
            _combatSaveAlignmentRecoveryRequested = false;
            _combatSaveAlignmentHydrating = false;
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
                    CompleteCombatSaveRestore();
                    _turnEndSynchronizationFailed = false;
                    _combatRestoreHeader = null; _combatRestoreCommitted = false;
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
            if (_combatSaveAlignment != null)
            {
                if (!IsActive || !IsCurrentCombatSave(_combatSaveAlignment)
                    || DateTime.UtcNow > _combatSaveDeadline)
                { _combatSaveAlignment = null; _combatSaveAlignmentAck = null; _combatSaveAlignmentRecoveryRequested = false;
                    _combatSaveAlignmentHydrating = false; }
                else TryAcknowledgeCombatSaveTurnAlignment();
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
