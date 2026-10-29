using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProtoBuf;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Localization;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Networking.Messages.Lobby;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerHost
    {
        private sealed class Rejoin
        {
            internal NetworkGame Session;
            internal long Peer;
            internal NotifySessionRejoin Header;
            internal RealTimeNativeCheckpointSender Sender;
            internal bool Prepared, Loaded, Stopped;
            internal DateTime Deadline, NextHeader;
            internal List<string> Units = [];
            internal string OldToken;
        }
        private Rejoin _rejoin;
        private NetworkGame _pendingRejoinSession;
        private long _pendingRejoinPeer;
        private string _pendingRejoinToken;
        private DateTime _nextRejoinAttempt;
        private readonly Dictionary<long, string> _rejoinTokens = new();
        private readonly Dictionary<string, List<string>> _disconnectedOwnership = new(StringComparer.Ordinal);

        private void RememberDisconnectedOwnership(long peer)
        {
            if (!_rejoinTokens.TryGetValue(peer, out var token)) return;
            _disconnectedOwnership[token] = _rejoin?.Peer == peer ? [.. _rejoin.Units]
                : Game.Characters.Where(c => c.Owner?.Id == peer).Select(c => c.UnitId).ToList();
            _rejoinTokens.Remove(peer);
            if (_rejoin?.Peer == peer) ResetSessionRejoin();
        }
        private void ResetSessionRejoin()
        {
            if (ReferenceEquals(SessionRejoinRuntime.HostOwner, _rejoin)) SessionRejoinRuntime.HostOwner = null;
            _rejoin?.Sender?.Dispose(); _rejoin = null;
        }
        private void QueueSessionRejoin(long peer, string oldToken)
        {
            var session = Game;
            _mainThreadAccessor.Post(() =>
            {
                lock (ActionLock)
                {
                    if (!ReferenceEquals(Game, session) || GetPlayer(peer) == null) return;
                    _pendingRejoinSession = session; _pendingRejoinPeer = peer;
                    _pendingRejoinToken = oldToken; _nextRejoinAttempt = DateTime.MinValue;
                    Logger.LogInformation("[Rejoin] Connection queued until host reaches a capturable state. Peer={Peer}", peer);
                    PlayerNotification.ShowWarningNotification(WellKnownKeys.GameNotifications.Dialogs.RejoinWaiting.Key, addToLog: false);
                    if (_rejoinTokens.TryGetValue(peer, out var ticket))
                        Send(peer, new NotifySessionRejoin { Stage = "Waiting", ConnectionToken = ticket });
                }
            });
        }
        private async Task BeginSessionRejoinAsync(long peer, string oldToken)
        {
            if (!RealTimePresentationRuntime.Enabled || Game?.Stage != NetworkLobbyStage.Playing || _rejoin != null) return;
            var client = GetPlayer(peer); var host = GetHost();
            if (client == null || Game.Players.Count != 2
                || RealTimeContentCompatibility.Compare(host.ContentState, client.ContentState) != RealTimeContentMismatch.None
                || client.ContentState.DiscrepantDLCs.Any() || client.ContentState.DiscrepantMods.Any()) return;
            var session = Game; var combat = Game.Combat; var area = Game.CurrentArea;
            var job = new Rejoin { Session = session, Peer = peer, OldToken = oldToken,
                Deadline = DateTime.UtcNow.AddMinutes(5) };
            if (oldToken != null && oldToken.Length == 32 && _disconnectedOwnership.TryGetValue(oldToken, out var units)) job.Units = [.. units];
            _rejoin = job; SessionRejoinRuntime.HostOwner = job;
            bool Owned()
            {
                lock (ActionLock) return ReferenceEquals(_rejoin, job) && ReferenceEquals(Game, session) && IsActive
                    && ReferenceEquals(Game.Combat, combat) && ReferenceEquals(Game.CurrentArea, area)
                    && Game.Players.Any(p => p.Id == peer) && !job.Stopped;
            }
            try
            {
                var paused = GameInteraction.IsPaused;
                await GameInteraction.SetPauseAsync(true);
                PlayerNotification.ShowWarningNotification(WellKnownKeys.GameNotifications.Dialogs.RejoinSync.Key, addToLog: false);
                var reference = await RejoinMainThread(() =>
                {
                    if (!Owned() || area == null || area.IsGlobalMap || combat != null && !IsRealTimeCombatActive)
                        throw new InvalidOperationException("rejoin-requires-local-area-and-no-turn-based-combat");
                    return CombatInteraction.GetRealTimeNativeReferenceState();
                });
                var referenceBytes = RealTimeNativeReferenceCodec.Encode(Mapper.Map<Networking.Messages.Contracts.NetworkCombatState>(reference));
                var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = reference });
                var checkpoint = Guid.NewGuid().ToString("N");
                using var source = await RealTimeNativeHostCapture.CaptureAsync(_mainThreadAccessor, CombatInteraction, session.Id, checkpoint, Owned);
                var archive = await Task.Run(() => SessionRejoinRuntime.Zip(source));
                _mainThreadAccessor.Post(() =>
                {
                    try
                    {
                        if (!Owned()) return;
                        var actual = CombatInteraction.GetRealTimeNativeReferenceState();
                        if (RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = actual }) != hash
                            || RealTimeCombatTemporalDiff.Summarize(reference, actual) != null)
                            throw new InvalidOperationException("rejoin-host-changed-during-capture");
                        var manifest = new NotifyRealTimeNativeCheckpoint
                        {
                            Stage = (int)RealTimeNativeCheckpointStage.Manifest, SessionId = session.Id,
                            LoadedSaveSeed = session.LoadedSaveSeed, Epoch = Math.Max(1, session.RealTimeCombat.Epoch),
                            CheckpointId = checkpoint, BuildFingerprint = host.ContentState.RealTimeBuildFingerprint,
                            CanonicalStateHash = hash, ArchiveHash = RealTimeNativeCheckpointLimits.Hash(archive),
                            ArchiveBytes = archive.Length, ChunkBytes = RealTimeNativeCheckpointLimits.ChunkBytes,
                            ChunkCount = (archive.Length + RealTimeNativeCheckpointLimits.ChunkBytes - 1) / RealTimeNativeCheckpointLimits.ChunkBytes
                        };
                        job.Header = new NotifySessionRejoin { Stage = "Transfer", Manifest = manifest, AreaId = area.Id,
                            AreaSeed = area.Seed, CombatSeed = combat?.Seed ?? 0, CombatRound = combat?.Round ?? 0,
                            RealTime = combat != null, Paused = paused, Reference = referenceBytes,
                            ConnectionToken = _rejoinTokens[peer] };
                        job.Sender = new RealTimeNativeCheckpointSender(peer, manifest, archive);
                        Logger.LogInformation("Live rejoin checkpoint ready; host world retained. Peer={Peer}, Checkpoint={Checkpoint}, Bytes={Bytes}", peer, checkpoint, archive.Length);
                    }
                    catch (Exception error) { StopSessionRejoin(job, error.Message); }
                });
            }
            catch (Exception error) { _mainThreadAccessor.Post(() => StopSessionRejoin(job, error.Message)); }
        }
        private Task<T> RejoinMainThread<T>(Func<T> action)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainThreadAccessor.Post(() => { try { completion.TrySetResult(action()); } catch (Exception error) { completion.TrySetException(error); } });
            return completion.Task;
        }
        private void TickSessionRejoin()
        {
            lock (ActionLock) TickSessionRejoinLocked();
        }
        private void TickSessionRejoinLocked()
        {
            if (_pendingRejoinSession != null)
            {
                if (!IsActive || !ReferenceEquals(Game, _pendingRejoinSession) || GetPlayer(_pendingRejoinPeer) == null)
                    _pendingRejoinSession = null;
                else if (_rejoin == null && DateTime.UtcNow >= _nextRejoinAttempt)
                {
                    _nextRejoinAttempt = DateTime.UtcNow.AddSeconds(1);
                    // A TB turn, dialogue or loot window cannot be captured as an
                    // RT presentation checkpoint. Keep the host playable until safe.
                    if (Game.Stage == NetworkLobbyStage.Playing && Game.CurrentArea?.IsGlobalMap == false
                        && (Game.Combat == null || IsRealTimeCombatActive)
                        && Game.DialogState == null
                        && (GameInteraction.CurrentGameMode == Kingmaker.GameModes.GameModeType.Default
                            || GameInteraction.CurrentGameMode == Kingmaker.GameModes.GameModeType.Pause))
                    {
                        var peer = _pendingRejoinPeer; var token = _pendingRejoinToken;
                        _pendingRejoinSession = null;
                        _ = BeginSessionRejoinAsync(peer, token);
                    }
                }
            }
            var job = _rejoin;
            if (job == null) return;
            if (!ReferenceEquals(Game, job.Session)) { ResetSessionRejoin(); return; }
            GameInteraction.SetPause(true);
            if (job.Stopped) return;
            if (DateTime.UtcNow > job.Deadline) { StopSessionRejoin(job, "rejoin-timeout"); return; }
            if (job.Header == null || job.Loaded && job.Header.RealTime) return;
            try
            {
                if (DateTime.UtcNow >= job.NextHeader)
                {
                    Send(job.Peer, job.Header); job.NextHeader = DateTime.UtcNow.AddSeconds(1);
                }
                if (job.Prepared && !job.Loaded)
                    foreach (var chunk in job.Sender.GetDueChunks(DateTime.UtcNow))
                        Send(job.Peer, new NotifySessionRejoinChunk { Chunk = chunk });
            }
            catch (Exception error) { StopSessionRejoin(job, error.Message); }
        }
        private void OnSessionRejoinAck(long peer, ClientSessionRejoinAck frame)
        {
            var ack = frame?.Ack == null ? null : Serializer.DeepClone(frame.Ack);
            _mainThreadAccessor.Post(() =>
            {
                lock (ActionLock)
                {
                var job = _rejoin;
                if (job?.Header == null || job.Stopped || job.Peer != peer
                    || !RealTimeNativeCheckpointLimits.Matches(job.Header.Manifest, ack)) return;
                if (ack.Stage == (int)RealTimeNativeCheckpointAckStage.Failed) { StopSessionRejoin(job, ack.Reason ?? "rejoin-client-failed"); return; }
                if (ack.Stage == (int)RealTimeNativeCheckpointAckStage.Prepared) { job.Prepared = true; return; }
                if (ack.Stage == (int)RealTimeNativeCheckpointAckStage.Loaded && job.Sender.IsTransferred && !job.Loaded)
                {
                    if (ack.CanonicalStateHash != job.Header.Manifest.CanonicalStateHash) { StopSessionRejoin(job, "rejoin-loaded-state-mismatch"); return; }
                    try
                    {
                        var reference = Mapper.Map<Entities.Combat.NetworkCombatState>(RealTimeNativeReferenceCodec.Decode(job.Header.Reference));
                        var current = CombatInteraction.GetRealTimeNativeReferenceState();
                        if (RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = current }) != job.Header.Manifest.CanonicalStateHash
                            || RealTimeCombatTemporalDiff.Summarize(reference, current) != null
                            || RealTimeNativeTopologyComparison.Difference(reference.NativeTopology, current.NativeTopology) != null)
                            throw new InvalidOperationException("rejoin-host-changed-before-commit");
                    }
                    catch (Exception error) { StopSessionRejoin(job, error.Message); return; }
                    job.Loaded = true;
                    var player = GetPlayer(peer);
                    foreach (var character in Game.Characters.Where(c => job.Units.Contains(c.UnitId)))
                    {
                        character.Owner = player; Game.CharactersOwnershipHistory[character.UnitId] = peer;
                    }
                    UpdateLobbySyncStatus(player, NetworkLobbySyncStatus.Succeed);
                    Send(CreateNotifyLobbyPlayersChanged());
                    Send(new NotifySaveGameSyncStatusChanged { PlayerId = peer, Status = NetworkLobbySyncStatus.Succeed.ToString() });
                    Send(new NotifyLobbyCharactersChanged { Characters = Mapper.Map<List<Networking.Messages.Contracts.NetworkCharacter>>(Game.Characters) });
                    foreach (var character in Game.Characters.Where(c => job.Units.Contains(c.UnitId)))
                        Send(new NotifyCharacterOwnerChanged { Character = Mapper.Map<Networking.Messages.Contracts.NetworkCharacter>(character) });
                    GameInteraction.ReselectSelectedCharacters();
                    if (job.OldToken != null) _disconnectedOwnership.Remove(job.OldToken);
                    if (job.Header.RealTime) { _waitingForRealTimePeer = true; TickRealTimeReconnection(); }
                    else { job.Header.Stage = "Resume"; job.NextHeader = default; }
                    return;
                }
                if (ack.Stage == (int)RealTimeNativeCheckpointAckStage.Committed && job.Loaded && !job.Header.RealTime)
                {
                    var paused = job.Header.Paused; ResetSessionRejoin(); Game.ForcedPause = null;
                    GameInteraction.SetPause(paused);
                    Logger.LogInformation("Live rejoin completed without reloading host."); return;
                }
                job.Sender.Acknowledge(peer, ack);
                }
            });
        }
        private void StopSessionRejoin(Rejoin job, string reason)
        {
            if (!ReferenceEquals(_rejoin, job)) return;
            job.Stopped = true; GameInteraction.SetPause(true);
            if (job.Header != null) Send(job.Peer, new NotifySessionRejoin { Stage = "Failed", Manifest = job.Header.Manifest,
                ConnectionToken = job.Header.ConnectionToken, Reason = reason?.Substring(0, Math.Min(160, reason.Length)) });
            Logger.LogError("Live rejoin stopped; host not reloaded. Reason={Reason}", reason);
            PlayerNotification.ShowWarningNotification(WellKnownKeys.GameNotifications.Dialogs.RejoinFailed.Key, addToLog: false);
        }
    }
}
