using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using ProtoBuf;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services.CombatSaving;
using WOTRMultiplayer.Services.GameInteraction;
using WOTRMultiplayer.Services.Random;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerHost
    {
        private HostCombatSaveRuntime _combatSave;

        internal async void RequestCombatSaveFromMenu()
        {
            try
            {
                PlayerNotification.ShowWarningNotification(WellKnownKeys.LobbyWindow.RealTime.SaveCombatWorking.Key, addToLog: false);
                var result = await SaveCombatAsync();
                _mainThreadAccessor.Post(() => PlayerNotification.ShowWarningNotification(CombatSaveMessages.ForResult(result), addToLog: false));
            }
            catch (Exception error) { Logger.LogError(error, "[CombatSave] Menu save request failed"); }
        }

        // The coordinator validates authority and mechanical readiness on every request.
        public async Task<string> SaveCombatAsync()
        {
            if (!CombatSaveActivation.Enabled) return "restoration-not-validated";
            if (_combatSave != null) return "busy";
            var runtime = new HostCombatSaveRuntime(this);
            _combatSave = runtime;
            try
            {
                var result = await new CombatSaveCoordinator(runtime).SaveAsync();
                if (result == "divergence" && await runtime.RecoverUnchangedHostTurnAsync())
                    return "recovery-started";
                return result;
            }
            catch (Exception error) { Logger.LogError(error, "[CombatSave] Save failed"); return "failed"; }
            finally { if (ReferenceEquals(_combatSave, runtime)) _combatSave = null; }
        }

        private void OnCombatSaveAck(long peer, ClientCombatSaveAck message)
        {
            if (message?.Identity == null || message.Digest?.Length > 64 || message.Refusal?.Length > 128
                || message.Identity.Operation?.Length != 32 || message.Identity.SessionId?.Length > 128) return;
            var frame = Serializer.DeepClone(message);
            _mainThreadAccessor.Post(() =>
            {
                if (frame.Identity.Stage?.StartsWith("Restore", StringComparison.Ordinal) == true)
                    AcceptCombatRestoreAck(peer, frame);
                else _combatSave?.Accept(peer, frame);
            });
        }

        private sealed class HostCombatSaveRuntime : ICombatSaveRuntime
        {
            private readonly MultiplayerHost _host;
            private readonly NetworkGame _session;
            private NotifyCombatSave _header;
            private readonly HashSet<long> _peers = new();
            private readonly Dictionary<long, CombatSaveAcknowledgement> _acks = new();
            private TaskCompletionSource<IReadOnlyCollection<CombatSaveAcknowledgement>> _ready;
            private CombatSavePauseLease _pause;
            private bool _finished;
            private bool _recovering;
            private string _initialDigest;
            internal HostCombatSaveRuntime(MultiplayerHost host) { _host = host; _session = host.Game; }
            public Task<T> OnMainThreadAsync<T>(Func<T> action) => _host.RejoinMainThread(action);
            public IReadOnlyCollection<long> ConnectedPeers => _host.Game.Players
                .Where(p => p.Id != _host.Game.LocalPlayerId).Select(p => p.Id).ToArray();
            public string GetRefusal() => !_host.IsActive || !ReferenceEquals(_host.Game, _session) ? "session-changed"
                : (!_recovering && _host.IsSavingBlockedByCombatSync) || _host._rejoin != null || _host._nativeRecoveryOwner != null
                    || _host._waitingForRealTimePeer ? "synchronization-required"
                    : _host.CombatSaveRefusal() ?? (_recovering || CombatSaveNativePermission.Check(_host) ? null : "native-save-forbidden");
            public CombatSaveIdentity CreateIdentity()
            {
                _header = new NotifyCombatSave { SessionId = _session.Id, SessionSeed = _session.SessionSeed,
                    LoadedSaveSeed = _session.LoadedSaveSeed, Epoch = _session.RealTimeCombat.Epoch,
                    Operation = Guid.NewGuid().ToString("N"), Stage = "Prepare" };
                _peers.UnionWith(ConnectedPeers);
                _host.Logger.LogInformation("[CombatSave] Preparing. Operation={Operation}, Round={Round}, Unit={Unit}",
                    _header.Operation, _session.Combat.Round, _session.Combat.Turn.UnitId);
                return SaveIdentity(_header);
            }
            public IDisposable AcquireOwnedPause(CombatSaveIdentity identity)
                => _pause = new CombatSavePauseLease(() => _host.Game == _session && _session.ForcedPause == null
                    && !_host.IsSavingBlockedByCombatSync && !RealTimeNativeGraphRepair.RequiresRebuild(Kingmaker.Game.Instance.State));
            public bool IsCurrent(CombatSaveIdentity identity) => (!_finished || _recovering) && _host.IsActive
                && ReferenceEquals(_host.Game, _session) && _host.IsCurrentCombatSave(_header)
                && SaveIdentity(_header).Equals(identity);
            public string CaptureDigest()
            {
                var digest = _host.CaptureCombatSaveDigest();
                _initialDigest ??= digest;
                return digest;
            }

            internal async Task<bool> RecoverUnchangedHostTurnAsync()
            {
                // A peer mismatch cancels the manual save. Reconstruct both worlds
                // from a private host checkpoint only if the authoritative turn is
                // still exactly the frozen pre-request state. Never bless a changed host.
                IDisposable pause = null;
                try
                {
                    var ready = await OnMainThreadAsync(() =>
                    {
                        if (_header == null || !_host.IsActive || _host.Game != _session
                            || !_peers.SetEquals(ConnectedPeers) || _host.CombatSaveRefusal() != null
                            || _initialDigest == null || CaptureDigest() != _initialDigest) return false;
                        _recovering = true;
                        pause = new CombatSavePauseLease(() => false);
                        CombatSaveLoadState.Owner = _host;
                        CombatSaveLoadState.BindLoadedWorld(Kingmaker.Game.Instance.State);
                        return true;
                    });
                    if (!ready) return false;
                    var path = await WriteNewSaveAsync(SaveIdentity(_header), _initialDigest, CancellationToken.None);
                    return await OnMainThreadAsync(() =>
                    {
                        if (!IsCurrent(SaveIdentity(_header)) || !_peers.SetEquals(ConnectedPeers)
                            || CaptureDigest() != _initialDigest) return false;
                        _host.Logger.LogWarning("[CombatSave] Rebuilding both peers from unchanged host turn; no manual save published. Checkpoint={Path}", path);
                        // ForceLoadGame only resets the network session and transfers
                        // the archive. Enter through the native load path as a manual
                        // load does, so the host also replaces its world; its prefix
                        // performs the network transfer exactly once.
                        if (_host.GameInteraction.QuickLoadGame(path) != _session.Id)
                            throw new InvalidOperationException("combat-save-recovery-load-not-started");
                        return true;
                    });
                }
                catch (Exception error)
                {
                    _host.Logger.LogError(error, "[CombatSave] Authoritative turn recovery failed; old world stays protected.");
                    return false;
                }
                finally
                {
                    await OnMainThreadAsync(() => { pause?.Dispose(); _recovering = false; return true; });
                }
            }
            public async Task<IReadOnlyCollection<CombatSaveAcknowledgement>> PreparePeersAsync(CombatSaveIdentity identity, CancellationToken cancellation)
            {
                await OnMainThreadAsync(() =>
                {
                    _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    foreach (var peer in _peers) _host.Send(peer, _header);
                    if (_peers.Count == 0) _ready.TrySetResult(Array.Empty<CombatSaveAcknowledgement>());
                    return true;
                });
                using (cancellation.Register(() => _ready.TrySetCanceled())) return await _ready.Task;
            }
            internal void Accept(long peer, ClientCombatSaveAck frame)
            {
                if (_finished || _header == null || !_peers.Contains(peer) || _host.GetPlayer(peer) == null
                    || !_host.IsCurrentCombatSave(frame.Identity) || frame.Identity.Stage != "Prepare"
                    || frame.Identity.Operation != _header.Operation || _ready == null || _ready.Task.IsCompleted) return;
                if (frame.Refusal != null)
                {
                    _host.Logger.LogInformation("[CombatSave] Peer refused preparation. Peer={Peer}, Reason={Reason}", peer, frame.Refusal);
                    _ready.TrySetException(new CombatSaveRefusedException(frame.Refusal)); return;
                }
                if (string.IsNullOrWhiteSpace(frame.Digest) || frame.Digest.Length != 64 || !frame.Digest.All(Uri.IsHexDigit)) return;
                if (_acks.ContainsKey(peer)) return;
                _acks.Add(peer, new CombatSaveAcknowledgement { Identity = SaveIdentity(frame.Identity), Player = peer, Digest = frame.Digest });
                _host.Logger.LogInformation("[CombatSave] Peer prepared. Operation={Operation}, Peer={Peer}, Digest={Digest}", _header.Operation, peer, frame.Digest);
                if (_acks.Count == _peers.Count) _ready.TrySetResult(_acks.Values.ToArray());
            }
            public async Task<string> WriteNewSaveAsync(CombatSaveIdentity identity, string digest, CancellationToken cancellation)
            {
                var metadata = await OnMainThreadAsync(() =>
                {
                    if (!IsCurrent(identity) || GetRefusal() != null) throw new InvalidOperationException("combat-save-obsolete");
                    var generator = _host.ValueGenerator as DeterministicValueGenerator
                        ?? throw new NotSupportedException("combat-save-generator-unsupported");
                    var content = _host.GetInstalledContent();
                    return new CombatSaveManifest
                    {
                        Protocol = NetworkRealTimeCombatState.ProtocolVersion, BuildFingerprint = content.RealTimeBuildFingerprint,
                        GameVersion = Kingmaker.GameVersion.GetVersion(), Campaign = _session.Id,
                        Round = _session.Combat.Round, Unit = _session.Combat.Turn.UnitId,
                        CanonicalDigest = digest, RestoreDigest = _host.CaptureCombatSaveRestoreDigest(),
                        Generators = generator.CaptureCheckpoint(),
                        Owners = _session.Characters.Where(c => c.Owner != null).ToDictionary(c => c.UnitId, c => c.Owner.Id),
                        OwnerIsHost = _session.Characters.Where(c => c.Owner != null).ToDictionary(c => c.UnitId, c => c.Owner.IsHost),
                        NetworkCombatJson = JsonConvert.SerializeObject(_session.Combat),
                        NativeSupplement = CombatSaveNativeSupplement.Capture(Kingmaker.Game.Instance.TurnBasedCombatController),
                        Seeds = _host.CaptureCombatSaveSeeds()
                    };
                });
                bool Authorized() => !cancellation.IsCancellationRequested && IsCurrent(identity)
                    && _peers.SetEquals(ConnectedPeers) && GetRefusal() == null;
                using var source = await RealTimeNativeHostCapture.CaptureTurnBasedAsync(_host._mainThreadAccessor,
                    _host.CombatInteraction, _session.Id, _header.Operation, Authorized);
                var folder = await OnMainThreadAsync(() => Path.Combine(Kingmaker.Utility.ApplicationPaths.persistentDataPath,
                    _recovering ? "Cache/CombatRecovery" : "Saved Games"));
                if (_recovering) Directory.CreateDirectory(folder);
                var path = await Task.Run(() => CombatSaveArchive.WriteNew(folder, source, metadata, DateTime.Now,
                    () => OnMainThreadAsync(() =>
                    {
                        var authorized = Authorized();
                        var currentDigest = CaptureDigest();
                        var currentRestoreDigest = _host.CaptureCombatSaveRestoreDigest();
                        var generators = ((DeterministicValueGenerator)_host.ValueGenerator).CaptureCheckpoint();
                        var generatorsMatch = JsonConvert.SerializeObject(generators) == JsonConvert.SerializeObject(metadata.Generators);
                        if (!authorized || currentDigest != digest
                            || currentRestoreDigest != metadata.RestoreDigest || !generatorsMatch)
                        {
                            _host.Logger.LogWarning("[CombatSave] Publication refused. Authorized={Authorized}, Refusal={Refusal}, DigestBefore={Before}, DigestAfter={After}, GeneratorsMatch={GeneratorsMatch}",
                                authorized, GetRefusal(), digest, currentDigest, generatorsMatch);
                            if (!generatorsMatch && CombatSaveActivation.Enabled)
                            {
                                var root = _host.GameInteraction.GetPersistentDataPath();
                                File.WriteAllText(Path.Combine(root, "combat-save-generators-before.json"), JsonConvert.SerializeObject(metadata.Generators));
                                File.WriteAllText(Path.Combine(root, "combat-save-generators-after.json"), JsonConvert.SerializeObject(generators));
                            }
                        }
                        return authorized && currentDigest == digest
                            && currentRestoreDigest == metadata.RestoreDigest && generatorsMatch;
                    }).GetAwaiter().GetResult()), cancellation);
                await OnMainThreadAsync(() =>
                {
                    if (_recovering) return true; // Private recovery cache, never a player save slot.
                    // Update only the local catalog. The native global rescan/save
                    // paths can invoke cloud synchronization or upload callbacks.
                    var manager = Kingmaker.Game.Instance.SaveManager;
                    var info = manager.LoadZipSave(path) ?? throw new InvalidDataException("combat-save-list-header-unreadable");
                    lock (manager.m_Lock)
                        if (!manager.m_SavedGames.Any(s => string.Equals(s.FolderName, path, StringComparison.OrdinalIgnoreCase)))
                            manager.m_SavedGames.Add(info);
                    Kingmaker.EntitySystem.Persistence.SaveManager.RaiseUpdated();
                    return true;
                });
                return path;
            }
            public void DivergenceDetected(CombatSaveIdentity identity)
            {
                _pause.Protect();
                _host._turnEndSynchronizationFailed = true;
                CombatSaveLoadState.Owner = _host;
                CombatSaveLoadState.BindLoadedWorld(Kingmaker.Game.Instance.State);
                _host.Logger.LogError("[CombatSave] Divergence detected; no save written. Operation={Operation}", _header.Operation);
            }
            public void Finished(CombatSaveIdentity identity, string result, string path)
            {
                _finished = true;
                var finish = Serializer.DeepClone(_header); finish.Stage = "Finish"; finish.Result = result;
                if (RealTimeNativeGraphRepair.RequiresRebuild(Kingmaker.Game.Instance?.State))
                {
                    _pause?.Protect(); _host._turnEndSynchronizationFailed = true;
                    finish.Result = "divergence";
                }
                foreach (var peer in _peers) if (_host.GetPlayer(peer) != null) _host.Send(peer, finish);
                _host.Logger.LogInformation("[CombatSave] Finished. Operation={Operation}, Result={Result}, Path={Path}", _header.Operation, result, path);
            }
        }
    }
}
