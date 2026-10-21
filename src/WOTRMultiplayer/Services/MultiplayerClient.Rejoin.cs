using System;
using System.IO;
using Microsoft.Extensions.Logging;
using ProtoBuf;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Localization;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerClient
    {
        private string _rejoinToken;
        private NotifySessionRejoin _rejoinHeader;
        private RealTimeNativeCheckpointReceiver _rejoinReceiver;
        private bool _rejoinLoadStarted, _rejoinLoaded, _rejoinResumed, _rejoinFailed;
        private DateTime _rejoinDeadline;
        private void RestoreRejoinToken()
        {
            try
            {
                var path = Path.Combine(GameInteraction.GetPersistentDataPath(), "RTwP-Rejoin", "session-" + Game.SessionSeed + ".token");
                var token = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
                if (Guid.TryParseExact(token, "N", out _)) _rejoinToken = token;
            }
            catch (Exception error) { Logger.LogWarning("Could not read local reconnection ticket. Error={Error}", error.GetType().Name); }
        }
        private void PersistRejoinToken()
        {
            try
            {
                if (!Guid.TryParseExact(_rejoinToken, "N", out _)) return;
                var folder = Path.Combine(GameInteraction.GetPersistentDataPath(), "RTwP-Rejoin");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "session-" + Game.SessionSeed + ".token"), _rejoinToken);
            }
            catch (Exception error) { Logger.LogWarning("Could not persist reconnection ticket; automatic ownership recovery after client restart may be unavailable. Error={Error}", error.GetType().Name); }
        }
        private void ResetSessionRejoin()
        {
            if (ReferenceEquals(SessionRejoinRuntime.ClientOwner, this))
            { SessionRejoinRuntime.ClientOwner = null; SessionRejoinRuntime.ClientLoadPath = null; SessionRejoinRuntime.ClientSaveInfo = null; }
            _rejoinReceiver?.Dispose(); _rejoinReceiver = null; _rejoinHeader = null;
            _rejoinLoadStarted = _rejoinLoaded = _rejoinResumed = _rejoinFailed = false;
        }
        private void OnSessionRejoin(long peer, NotifySessionRejoin message)
        {
            if (peer == NetworkConstants.HostPlayerId && RealTimePresentationRuntime.Enabled
                && message?.Stage == "Waiting" && Guid.TryParseExact(message.ConnectionToken, "N", out _)
                && message.ConnectionToken == _rejoinToken)
            {
                var token = _rejoinToken;
                _mainThreadAccessor.Post(() =>
                {
                    if (IsActive && token == _rejoinToken)
                        PlayerNotification.ShowWarningNotification(WellKnownKeys.GameNotifications.Dialogs.RejoinWaiting.Key, addToLog: false);
                });
                return;
            }
            if (peer != NetworkConstants.HostPlayerId || !RealTimePresentationRuntime.Enabled
                || !RealTimeNativeCheckpointLimits.IsManifest(message?.Manifest)
                || message.Reference?.Length > RealTimeNativeReferenceCodec.MaximumBytes) return;
            var frame = Serializer.DeepClone(message);
            _mainThreadAccessor.Post(() =>
            {
                try
                {
                    if (frame.ConnectionToken != _rejoinToken || !Guid.TryParseExact(frame.ConnectionToken, "N", out _)
                        || frame.Manifest.BuildFingerprint != GetInstalledContent().RealTimeBuildFingerprint) return;
                    if (_rejoinHeader != null && !RealTimeNativeCheckpointLimits.SameIdentity(_rejoinHeader.Manifest, frame.Manifest)) return;
                    if (frame.Stage == "Failed") { FailSessionRejoin(frame.Reason ?? "rejoin-host-stopped"); return; }
                    if (frame.Stage == "Resume" && _rejoinLoaded && !_rejoinFailed && !_rejoinHeader.RealTime)
                    {
                        _rejoinResumed = true; Game.ForcedPause = null;
                        SessionRejoinRuntime.ClientOwner = null; SessionRejoinRuntime.ClientLoadPath = null;
                        SessionRejoinRuntime.ClientSaveInfo = null;
                        GameInteraction.SetPause(frame.Paused);
                        SendSessionRejoinAck(RealTimeNativeCheckpointAckStage.Committed); return;
                    }
                    if (frame.Stage != "Transfer" || _rejoinFailed) return;
                    if (_rejoinHeader == null)
                    {
                        if (string.IsNullOrEmpty(frame.AreaId) || frame.Reference == null) return;
                        _ = RealTimeNativeReferenceCodec.Decode(frame.Reference);
                        var prepare = RealTimeNativeCheckpointLimits.Copy(frame.Manifest);
                        prepare.Stage = (int)RealTimeNativeCheckpointStage.Prepare;
                        prepare.ArchiveHash = null; prepare.ArchiveBytes = prepare.ChunkBytes = prepare.ChunkCount = 0;
                        _rejoinReceiver = new RealTimeNativeCheckpointReceiver(prepare, frame.Manifest);
                        _rejoinHeader = frame; _rejoinDeadline = DateTime.UtcNow.AddMinutes(5);
                        SessionRejoinRuntime.ClientOwner = this;
                        GameInteraction.SetPause(true);
                        PlayerNotification.ShowWarningNotification(WellKnownKeys.GameNotifications.Dialogs.RejoinSync.Key, addToLog: false);
                    }
                    SendSessionRejoinAck(RealTimeNativeCheckpointAckStage.Prepared);
                    if (_rejoinLoadStarted) SendSessionRejoinAck(RealTimeNativeCheckpointAckStage.Received);
                    if (_rejoinLoaded) SendSessionRejoinAck(RealTimeNativeCheckpointAckStage.Loaded);
                }
                catch (Exception error) { FailSessionRejoin(error.Message); }
            });
        }
        private void OnSessionRejoinChunk(long peer, NotifySessionRejoinChunk message)
        {
            if (peer != NetworkConstants.HostPlayerId || message?.Chunk?.Data?.Length > RealTimeNativeCheckpointLimits.ChunkBytes) return;
            var chunk = message?.Chunk == null ? null : Serializer.DeepClone(message.Chunk);
            _mainThreadAccessor.Post(() =>
            {
                try
                {
                    if (_rejoinReceiver == null || _rejoinFailed || _rejoinResumed) return;
                    if (_rejoinLoadStarted) { SendSessionRejoinAck(RealTimeNativeCheckpointAckStage.Received); return; }
                    if (!_rejoinReceiver.Accept(chunk)) return;
                    SendSessionRejoinAck(RealTimeNativeCheckpointAckStage.Chunk, chunk.Number);
                    if (!_rejoinReceiver.IsComplete) return;
                    var archive = _rejoinReceiver.TakeCompleted();
                    _rejoinLoadStarted = true;
                    SendSessionRejoinAck(RealTimeNativeCheckpointAckStage.Received);
                    // Dedicated unique checkpoint, never the campaign's save slot.
                    var folder = Path.Combine(GameInteraction.GetPersistentDataPath(), "RTwP-Rejoin");
                    Directory.CreateDirectory(folder);
                    var path = Path.Combine(folder, _rejoinHeader.Manifest.CheckpointId + ".zks");
                    if (File.Exists(path)) throw new InvalidOperationException("rejoin-checkpoint-already-exists");
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(archive, 0, archive.Length);
                    SessionRejoinRuntime.ClientLoadPath = path;
                    Game.Id = _rejoinHeader.Manifest.SessionId; Game.LoadedSaveSeed = _rejoinHeader.Manifest.LoadedSaveSeed;
                    var inGame = Kingmaker.Game.Instance.Player?.IsInGame == true;
                    SetLobbyStage(NetworkLobbyStage.Loading);
                    var id = inGame ? GameInteraction.QuickLoadGame(path) : GameInteraction.LoadGameFromMainMenu(path);
                    if (id != Game.Id) throw new InvalidOperationException("rejoin-load-campaign-mismatch");
                    Logger.LogInformation("Live rejoin client loading verified checkpoint. Checkpoint={Checkpoint}", _rejoinHeader.Manifest.CheckpointId);
                }
                catch (Exception error) { FailSessionRejoin(error.Message); }
            });
        }
        private bool CompleteSessionRejoinLoad()
        {
            if (_rejoinHeader == null || !_rejoinLoadStarted || _rejoinResumed) return false;
            if (_rejoinLoaded || _rejoinFailed) return true;
            try
            {
                GameInteraction.SetPause(true);
                var header = _rejoinHeader; var manifest = header.Manifest;
                if (Game.Id != manifest.SessionId || Game.CurrentArea?.Id != header.AreaId)
                    throw new InvalidOperationException("rejoin-loaded-area-mismatch");
                Game.CurrentArea.Seed = header.AreaSeed; Game.LoadedSaveSeed = manifest.LoadedSaveSeed;
                var reference = Mapper.Map<NetworkCombatState>(RealTimeNativeReferenceCodec.Decode(header.Reference));
                var actual = CombatInteraction.GetRealTimeCombatState();
                var world = Kingmaker.Game.Instance.State;
                actual.NativeTopology = RealTimeNativeGraphInspection.CaptureState(world.Units.All, world.AreaEffects,
                    () => ReferenceEquals(Kingmaker.Game.Instance.State, world) && GameInteraction.IsPaused);
                if (RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = actual }) != manifest.CanonicalStateHash
                    || RealTimeCombatTemporalDiff.Summarize(reference, actual) != null
                    || RealTimeNativeTopologyComparison.Difference(reference.NativeTopology, actual.NativeTopology) != null)
                    throw new InvalidOperationException("rejoin-loaded-state-mismatch");
                if (header.RealTime)
                {
                    Game.Combat = new NetworkCombat { Seed = header.CombatSeed, Round = header.CombatRound, Stage = NetworkCombatStage.Running };
                    Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
                    Game.RealTimeCombat.Epoch = manifest.Epoch;
                    Game.RealTimeCombat.Phase = CombatSynchronizationPhase.Stable;
                    Game.RealTimeCombat.RecoveryStatus = "reconnecting";
                }
                _rejoinLoaded = true;
                SendSessionRejoinAck(RealTimeNativeCheckpointAckStage.Loaded);
                Logger.LogInformation("Live rejoin client checkpoint loaded and compared; still paused pending host Commit.");
            }
            catch (Exception error) { FailSessionRejoin(error.Message); }
            return true;
        }
        private void TickSessionRejoin()
        {
            if (_rejoinHeader == null || _rejoinResumed) return;
            GameInteraction.SetPause(true);
            if (!_rejoinFailed && DateTime.UtcNow > _rejoinDeadline) FailSessionRejoin("rejoin-timeout");
        }
        private void SendSessionRejoinAck(RealTimeNativeCheckpointAckStage stage, int number = 0, string reason = null)
        {
            var m = _rejoinHeader?.Manifest; if (m == null) return;
            Send(new ClientSessionRejoinAck { Ack = new ClientRealTimeNativeCheckpointAck
            {
                SessionId = m.SessionId, LoadedSaveSeed = m.LoadedSaveSeed, Epoch = m.Epoch, CheckpointId = m.CheckpointId,
                ArchiveHash = m.ArchiveHash, Stage = (int)stage, Number = number, Reason = reason,
                CanonicalStateHash = m.CanonicalStateHash
            } });
        }
        private void FailSessionRejoin(string reason)
        {
            _rejoinFailed = true; SessionRejoinRuntime.ClientOwner = this; GameInteraction.SetPause(true);
            SendSessionRejoinAck(RealTimeNativeCheckpointAckStage.Failed, reason: reason?.Substring(0, Math.Min(160, reason.Length)));
            Logger.LogError("Live rejoin client stopped. Reason={Reason}; no host reload requested.", reason);
            PlayerNotification.ShowWarningNotification(WellKnownKeys.GameNotifications.Dialogs.RejoinFailed.Key, addToLog: false);
        }
    }
}
