using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Kingmaker.UI.Kingdom;
using Kingmaker.Utility;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Abstractions;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Abstractions.GameInteraction.CombatLog;
using WOTRMultiplayer.Abstractions.Hashing;
using WOTRMultiplayer.Abstractions.IO;
using WOTRMultiplayer.Abstractions.Random;
using WOTRMultiplayer.Abstractions.Settings;
using WOTRMultiplayer.Abstractions.Unity;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Area;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.Crusades;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Connectivity;
using WOTRMultiplayer.Entities.Content;
using WOTRMultiplayer.Entities.Dialogs;
using WOTRMultiplayer.Entities.Dungeon;
using WOTRMultiplayer.Entities.GlobalMap;
using WOTRMultiplayer.Entities.GlobalMap.Kingdom;
using WOTRMultiplayer.Entities.Inspect;
using WOTRMultiplayer.Entities.Items;
using WOTRMultiplayer.Entities.Leveling;
using WOTRMultiplayer.Entities.NewGame;
using WOTRMultiplayer.Entities.Rest;
using WOTRMultiplayer.Entities.Settings;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Abstractions;
using WOTRMultiplayer.Networking.Configuration;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Networking.Messages.Lobby;
using WOTRMultiplayer.Networking.Messages.Requests;
using WOTRMultiplayer.Services.GameInteraction.Contexts;
using WOTRMultiplayer.Services.Random;
using WOTRMultiplayer.UI;

namespace WOTRMultiplayer.Services
{
    public class MultiplayerHost : MultiplayerActorBase, IMultiplayerHost
    {
        private readonly INetworkHostConnection _networkHost;
        private readonly IHashService _hashService;
        private readonly IMainThreadAccessor _mainThreadAccessor;
        private readonly ConcurrentDictionary<(long Epoch, long Sequence), ulong> _realTimeSnapshotHashes = new();
        private readonly ConcurrentDictionary<(long Epoch, long Sequence), DateTime> _realTimeSnapshotSentAt = new();
        private readonly RealTimeRuleRollMailbox _realTimeRuleRolls = new();
        private readonly RealTimeEntityIdMailbox _realTimeEntityIds = new();
        private int _consecutiveMissingRealTimeReplies;
        private readonly object _realTimeCommandLock = new();
        private readonly object _realTimePauseLock = new();
        private readonly Dictionary<long, long> _lastRealTimePauseRequestByPlayer = [];
        private RealTimeCommandGate _realTimeCommandGate;
        private NetworkRealTimeCombatState _realTimeCommandGateState;
        private long _realTimeLocalCommandEpoch;
        private long _nextRealTimeLocalCommandSequence;
        private long _realTimePauseRequestEpoch;
        private long _nextLocalRealTimePauseRequestSequence;
        private DateTime _nextRealTimeSnapshotAt;
        private DateTime _realTimeModeSwitchDeadline;
        private bool _realTimeModeCommitInProgress;
        private NotifyRealTimeModeTransition _lastRealTimeModePrepare;
        private NotifyRealTimeModeTransition _lastRealTimeModeCommit;
        private NotifyRealTimeModeTransition _lastRealTimeModeComplete;
        private bool _realTimeModeCompleteIsAbort;
        private int _realTimeModeRetransmissions;
        private NotifyRealTimeModeTransition _lastRealTimeAbort;
        private DateTime _realTimeRepairDeadline;
        private RealTimeRepairStage? _realTimeRepairStage;
        private NotifyRealTimeRepair _lastRealTimeRepairMessage;
        private int _realTimeRepairRetransmissions;
        private int _realTimeRepairVerificationInProgress;
        private NetworkCombatState _realTimeRepairCheckpoint;
        private bool _realTimeRepairHasMismatch;
        private bool _realTimeCriticalFaulted;
        private long _realTimeAISequenceEpoch;
        private long _nextRealTimeAISequence;
        private NetworkCombat _autoModeAttemptCombat;

        private NetworkLobbyStage Status => Game?.Stage ?? NetworkLobbyStage.None;

        public bool IsActive => _networkHost.IsActive;

        public bool IsInLobby => IsActive && Status == NetworkLobbyStage.Lobby;

        public bool IsRealTimeCombatActive => IsActive && Game?.Combat?.Stage == NetworkCombatStage.Running
            && Game.RealTimeCombat?.Mode == CombatMode.RealTimeWithPause;

        public bool IsRealTimeCombatBarrierActive => IsActive && Game?.Combat?.Stage == NetworkCombatStage.Running
            && Game.RealTimeCombat != null && Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable;

        public bool IsSavingBlockedByCombatSync => IsActive && Game?.RealTimeCombat != null
            && (Game.RealTimeCombat.BlocksSaving
                || Game.Combat?.Stage == NetworkCombatStage.Running
                    && Game.RealTimeCombat.Mode == CombatMode.RealTimeWithPause);

        public int ResolveRealTimeDiceRoll(string key, Func<int> localRoll)
        {
            var result = localRoll();
            if (!IsRealTimeCombatActive || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable)
                return result;
            var epoch = Game.RealTimeCombat.Epoch;
            var sequence = _realTimeRuleRolls.Publish(epoch, key);
            Send(new NotifyRealTimeRuleRoll
            {
                Epoch = epoch, Key = key, Sequence = sequence, Result = result
            });
            return result;
        }

        public string ResolveRealTimeEntityId(string key, Func<string> localId)
        {
            var id = localId();
            if (!IsRealTimeCombatActive || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable)
                return id;
            var epoch = Game.RealTimeCombat.Epoch;
            var sequence = _realTimeEntityIds.Publish(epoch, key);
            Send(new NotifyRealTimeEntityId
            {
                Epoch = epoch, Key = key, Sequence = sequence, EntityId = id
            });
            return id;
        }

        protected override void ResetRealTimeRuleRolls()
        {
            _realTimeRuleRolls.Reset();
            _realTimeEntityIds.Reset();
            _realTimeCriticalFaulted = false;
        }

        public void PublishRealTimeCombatSnapshot()
        {
            CheckRealTimeModeSwitchTimeout();
            CheckRealTimeRepairTimeout();
            if (Game?.Combat?.Stage == NetworkCombatStage.Running
                && Game.RealTimeCombat != null
                && Game.RealTimeCombat.PreferredMode != Game.RealTimeCombat.Mode
                && Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Stable
                && !ReferenceEquals(_autoModeAttemptCombat, Game.Combat)
                && GetSyncedPlayers().Any())
            {
                _autoModeAttemptCombat = Game.Combat;
                _ = RequestRealTimeCombatModeAsync(Game.RealTimeCombat.PreferredMode);
            }
            if (Game?.Combat?.Stage != NetworkCombatStage.Running
                || Game.RealTimeCombat.Mode != CombatMode.RealTimeWithPause
                || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable
                || DateTime.UtcNow < _nextRealTimeSnapshotAt)
            {
                return;
            }

            _nextRealTimeSnapshotAt = DateTime.UtcNow.AddMilliseconds(250);

            var state = CombatInteraction.GetRealTimeCombatState();
            var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot
            {
                Combat = state,
                ActiveOrders = RealTimeAcceptedOrderLedger.Snapshot(Game.RealTimeCombat).ToList()
            });
            var coordinator = new RealTimeCombatCoordinator(Game.RealTimeCombat);
            var sequence = coordinator.PublishAuthoritativeSnapshot(hash);
            _realTimeSnapshotHashes[(Game.RealTimeCombat.Epoch, sequence)] = hash;
            _realTimeSnapshotSentAt[(Game.RealTimeCombat.Epoch, sequence)] = DateTime.UtcNow;
            foreach (var key in _realTimeSnapshotHashes.Keys)
            {
                if (key.Epoch != Game.RealTimeCombat.Epoch || key.Sequence < sequence - 32)
                {
                    _realTimeSnapshotHashes.TryRemove(key, out _);
                    if (_realTimeSnapshotSentAt.TryRemove(key, out _) && key.Epoch == Game.RealTimeCombat.Epoch)
                    {
                        Game.RealTimeCombat.MissingSnapshotReplies++;
                        _consecutiveMissingRealTimeReplies++;
                    }
                }
            }
            Send(new NotifyRealTimeCombatSnapshot
            {
                Epoch = Game.RealTimeCombat.Epoch,
                Sequence = sequence,
                CanonicalStateHash = hash,
                ResponseTimeMs = Game.RealTimeCombat.ResponseTimeMs,
                MissingReplies = Game.RealTimeCombat.MissingSnapshotReplies,
                State = Mapper.Map<Networking.Messages.Contracts.NetworkCombatState>(state)
            });
            if (_consecutiveMissingRealTimeReplies >= 3)
            {
                Logger.LogWarning("Three consecutive real-time snapshot replies expired; pausing for verification. Epoch={Epoch}, Sequence={Sequence}", Game.RealTimeCombat.Epoch, sequence);
                _consecutiveMissingRealTimeReplies = 0;
                _ = BeginRealTimeRepairAsync();
            }
        }

        public async Task<bool> RequestRealTimeCombatModeAsync(CombatMode mode)
        {
            if (!IsActive || Game?.Combat?.Stage != NetworkCombatStage.Running
                || !Enum.IsDefined(typeof(CombatMode), mode) || Game.RealTimeCombat == null
                || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable
                || Game.RealTimeCombat.Mode == mode)
            {
                return false;
            }

            _autoModeAttemptCombat = Game.Combat;

            var players = GetSyncedPlayers().Select(x => x.Id).Append(Game.LocalPlayerId).Distinct().ToList();
            var coordinator = new RealTimeCombatCoordinator(Game.RealTimeCombat);
            coordinator.RequestModeSwitch(mode, players);
            _consecutiveMissingRealTimeReplies = 0;
            _realTimeModeSwitchDeadline = DateTime.UtcNow.AddSeconds(30);
            _realTimeModeRetransmissions = 0;
            try
            {
                await GameInteraction.SetPauseAsync(true);
                _lastRealTimeModePrepare = new NotifyRealTimeModeTransition
                {
                    Epoch = Game.RealTimeCombat.Epoch,
                    Stage = (int)RealTimeModeTransitionStage.Prepare,
                    Mode = (int)mode
                };
                Send(_lastRealTimeModePrepare);
                if (coordinator.AcknowledgeBarrier(Game.LocalPlayerId, players))
                {
                    await CommitRealTimeModeSwitchAsync();
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to prepare real-time combat mode switch. Epoch={Epoch}, Mode={Mode}", Game.RealTimeCombat.Epoch, mode);
                await AbortRealTimeModeSwitchAsync();
                return false;
            }
        }

        private async Task CommitRealTimeModeSwitchAsync()
        {
            if (_realTimeModeCommitInProgress || Game?.RealTimeCombat?.Phase != CombatSynchronizationPhase.PreparingModeSwitch)
            {
                return;
            }
            _realTimeModeCommitInProgress = true;
            try
            {
                var state = await CombatInteraction.GetRealTimeCombatStateAsync();
                var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = state });
                var coordinator = new RealTimeCombatCoordinator(Game.RealTimeCombat);
                coordinator.CommitModeSwitch(hash);
                await GameInteraction.SetTurnBasedCombatModeAsync(Game.RealTimeCombat.Mode == CombatMode.TurnBased);
                _realTimeModeSwitchDeadline = DateTime.UtcNow.AddSeconds(30);
                _realTimeModeRetransmissions = 0;
                _lastRealTimeModeCommit = new NotifyRealTimeModeTransition
                {
                    Epoch = Game.RealTimeCombat.Epoch,
                    Stage = (int)RealTimeModeTransitionStage.Commit,
                    Mode = (int)Game.RealTimeCombat.Mode,
                    CanonicalStateHash = hash,
                    State = Mapper.Map<Networking.Messages.Contracts.NetworkCombatState>(state)
                };
                Send(_lastRealTimeModeCommit);
                var players = GetSyncedPlayers().Select(x => x.Id).Append(Game.LocalPlayerId).Distinct().ToList();
                if (coordinator.AcknowledgeModeCommit(Game.LocalPlayerId, players))
                {
                    CompleteRealTimeModeSwitch();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to commit real-time combat mode switch. Epoch={Epoch}", Game?.RealTimeCombat?.Epoch);
                await AbortRealTimeModeSwitchAsync();
            }
            finally
            {
                _realTimeModeCommitInProgress = false;
            }
        }

        private void CompleteRealTimeModeSwitch()
        {
            BeginRealTimeModeCompletion(isAbort: false);
        }

        private void BeginRealTimeModeCompletion(bool isAbort)
        {
            if (_lastRealTimeModeComplete != null)
            {
                return;
            }
            _realTimeModeCompleteIsAbort = isAbort;
            Game.RealTimeCombat.RequiredPlayers = GetSyncedPlayers().Select(x => x.Id)
                .Append(Game.LocalPlayerId).ToHashSet();
            Game.RealTimeCombat.ReadyPlayers = [Game.LocalPlayerId];
            _lastRealTimeModeComplete = new NotifyRealTimeModeTransition
            {
                Epoch = Game.RealTimeCombat.Epoch,
                Stage = (int)RealTimeModeTransitionStage.Complete,
                Mode = (int)Game.RealTimeCombat.Mode,
                CanonicalStateHash = Game.RealTimeCombat.CanonicalStateHash
            };
            _realTimeModeSwitchDeadline = DateTime.UtcNow.AddSeconds(10);
            Send(_lastRealTimeModeComplete);
            TryFinalizeRealTimeModeCompletion();
        }

        private void TryFinalizeRealTimeModeCompletion()
        {
            if (_lastRealTimeModeComplete == null)
            {
                return;
            }
            var connectedPlayers = GetSyncedPlayers().Select(x => x.Id)
                .Append(Game.LocalPlayerId).ToHashSet();
            Game.RealTimeCombat.RequiredPlayers.IntersectWith(connectedPlayers);
            if (!Game.RealTimeCombat.RequiredPlayers.IsSubsetOf(Game.RealTimeCombat.ReadyPlayers))
            {
                return;
            }
            if (_realTimeModeCompleteIsAbort)
            {
                Game.RealTimeCombat.Phase = CombatSynchronizationPhase.Stable;
                Game.RealTimeCombat.RequiredPlayers.Clear();
                Game.RealTimeCombat.ReadyPlayers.Clear();
            }
            else
            {
                new RealTimeCombatCoordinator(Game.RealTimeCombat).CompleteModeCommit();
            }
            _realTimeModeSwitchDeadline = default;
            _lastRealTimeModePrepare = null;
            _lastRealTimeModeCommit = null;
            _lastRealTimeModeComplete = null;
            _lastRealTimeAbort = null;
            GameInteraction.SetPause(Game.RealTimeCombat.IsManualPauseActive || Game.ForcedPause != null);
        }

        private async Task AbortRealTimeModeSwitchAsync()
        {
            if (Game?.RealTimeCombat == null || Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Stable)
            {
                return;
            }
            var requiredPlayers = GetSyncedPlayers().Select(x => x.Id).Append(Game.LocalPlayerId).Distinct().ToHashSet();
            var coordinator = new RealTimeCombatCoordinator(Game.RealTimeCombat);
            coordinator.AbortModeSwitch();
            _lastRealTimeModePrepare = null;
            _lastRealTimeModeCommit = null;
            Game.RealTimeCombat.Phase = CombatSynchronizationPhase.Repairing;
            Game.RealTimeCombat.RequiredPlayers = requiredPlayers;
            Game.RealTimeCombat.ReadyPlayers = [Game.LocalPlayerId];
            try
            {
                await GameInteraction.SetTurnBasedCombatModeAsync(Game.RealTimeCombat.Mode == CombatMode.TurnBased);
                var state = await CombatInteraction.GetRealTimeCombatStateAsync();
                var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = state });
                Game.RealTimeCombat.CanonicalStateHash = hash;
                _realTimeModeSwitchDeadline = DateTime.UtcNow.AddSeconds(30);
                _lastRealTimeAbort = new NotifyRealTimeModeTransition
                {
                    Epoch = Game.RealTimeCombat.Epoch,
                    Stage = (int)RealTimeModeTransitionStage.Abort,
                    Mode = (int)Game.RealTimeCombat.Mode,
                    CanonicalStateHash = hash,
                    State = Mapper.Map<Networking.Messages.Contracts.NetworkCombatState>(state)
                };
                Send(_lastRealTimeAbort);
                GameInteraction.SetPause(true);
                TryCompleteRealTimeAbort();
            }
            catch (Exception ex)
            {
                _realTimeModeSwitchDeadline = default;
                Logger.LogError(ex, "Combat-mode rollback could not be applied locally; the game remains paused and saving is blocked. Epoch={Epoch}", Game.RealTimeCombat.Epoch);
                GameInteraction.SetPause(true);
            }
        }

        private void CheckRealTimeModeSwitchTimeout()
        {
            if (_realTimeModeSwitchDeadline == default || DateTime.UtcNow <= _realTimeModeSwitchDeadline
                || Game?.RealTimeCombat?.Phase == CombatSynchronizationPhase.Stable)
            {
                return;
            }
            if (_lastRealTimeModeComplete != null)
            {
                TryFinalizeRealTimeModeCompletion();
                if (_lastRealTimeModeComplete == null)
                {
                    return;
                }
                _realTimeModeSwitchDeadline = DateTime.UtcNow.AddSeconds(10);
                Logger.LogWarning("Waiting for peer to acknowledge combat-mode completion. Epoch={Epoch}", Game.RealTimeCombat.Epoch);
                Send(_lastRealTimeModeComplete);
                return;
            }
            if (Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Repairing && _lastRealTimeAbort != null)
            {
                _realTimeModeSwitchDeadline = DateTime.UtcNow.AddSeconds(30);
                TryCompleteRealTimeAbort();
                if (Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Repairing)
                {
                    Logger.LogWarning("Waiting for peer to apply safe combat-mode abort. Epoch={Epoch}", Game.RealTimeCombat.Epoch);
                    Send(_lastRealTimeAbort);
                }
                return;
            }
            var pending = Game.RealTimeCombat.Phase switch
            {
                CombatSynchronizationPhase.PreparingModeSwitch => _lastRealTimeModePrepare,
                CombatSynchronizationPhase.Synchronizing => _lastRealTimeModeCommit,
                _ => null
            };
            if (pending != null && _realTimeModeRetransmissions++ < 2)
            {
                _realTimeModeSwitchDeadline = DateTime.UtcNow.AddSeconds(10);
                Logger.LogWarning("Retransmitting combat-mode barrier. Epoch={Epoch}, Stage={Stage}, Attempt={Attempt}", pending.Epoch, pending.Stage, _realTimeModeRetransmissions);
                Send(pending);
                return;
            }
            _realTimeModeSwitchDeadline = default;
            _ = AbortRealTimeModeSwitchAsync();
        }

        private void TryCompleteRealTimeAbort()
        {
            if (Game?.RealTimeCombat?.Phase != CombatSynchronizationPhase.Repairing || _lastRealTimeAbort == null)
            {
                return;
            }
            var currentPlayers = GetSyncedPlayers().Select(x => x.Id).Append(Game.LocalPlayerId).ToHashSet();
            if (!currentPlayers.IsSubsetOf(Game.RealTimeCombat.ReadyPlayers))
            {
                return;
            }
            _lastRealTimeAbort = null;
            BeginRealTimeModeCompletion(isAbort: true);
        }

        private void OnClientRealTimeModeTransitionAck(long receivedFrom, ClientRealTimeModeTransitionAck message)
        {
            _mainThreadAccessor.Post(async () =>
            {
                try
                {
                    if (Game?.RealTimeCombat == null || message == null
                        || message.PlayerId != receivedFrom || GetPlayer(receivedFrom) == null
                        || message.Epoch != Game.RealTimeCombat.Epoch)
                    {
                        return;
                    }
                    var players = GetSyncedPlayers().Select(x => x.Id).Append(Game.LocalPlayerId).Distinct().ToList();
                    var coordinator = new RealTimeCombatCoordinator(Game.RealTimeCombat);
                    if ((RealTimeModeTransitionStage)message.Stage == RealTimeModeTransitionStage.Prepare
                        && Game.RealTimeCombat.Phase == CombatSynchronizationPhase.PreparingModeSwitch)
                    {
                        if (!message.Applied)
                        {
                            await AbortRealTimeModeSwitchAsync();
                        }
                        else if (coordinator.AcknowledgeBarrier(receivedFrom, players))
                        {
                            await CommitRealTimeModeSwitchAsync();
                        }
                    }
                    else if ((RealTimeModeTransitionStage)message.Stage == RealTimeModeTransitionStage.Commit
                        && Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Synchronizing
                        && _lastRealTimeModeComplete == null)
                    {
                        if (!message.Applied || message.CanonicalStateHash != Game.RealTimeCombat.CanonicalStateHash)
                        {
                            Logger.LogWarning("Peer did not apply combat mode checkpoint. PlayerId={PlayerId}, Epoch={Epoch}, HostHash={HostHash}, ClientHash={ClientHash}", receivedFrom, message.Epoch, Game.RealTimeCombat.CanonicalStateHash, message.CanonicalStateHash);
                            await AbortRealTimeModeSwitchAsync();
                        }
                        else if (coordinator.AcknowledgeModeCommit(receivedFrom, players))
                        {
                            CompleteRealTimeModeSwitch();
                        }
                    }
                    else if ((RealTimeModeTransitionStage)message.Stage == RealTimeModeTransitionStage.Abort
                        && Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Repairing
                        && _lastRealTimeModeComplete == null)
                    {
                        if (message.Applied && message.CanonicalStateHash == Game.RealTimeCombat.CanonicalStateHash)
                        {
                            Game.RealTimeCombat.ReadyPlayers.Add(receivedFrom);
                            TryCompleteRealTimeAbort();
                        }
                        else
                        {
                            Logger.LogError("Peer could not reconcile aborted combat mode. PlayerId={PlayerId}, Epoch={Epoch}, HostHash={HostHash}, ClientHash={ClientHash}", receivedFrom, message.Epoch, Game.RealTimeCombat.CanonicalStateHash, message.CanonicalStateHash);
                        }
                    }
                    else if ((RealTimeModeTransitionStage)message.Stage == RealTimeModeTransitionStage.Complete
                        && _lastRealTimeModeComplete != null
                        && message.Applied && message.CanonicalStateHash == Game.RealTimeCombat.CanonicalStateHash
                        && Game.RealTimeCombat.RequiredPlayers.Contains(receivedFrom))
                    {
                        Game.RealTimeCombat.ReadyPlayers.Add(receivedFrom);
                        TryFinalizeRealTimeModeCompletion();
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error processing combat-mode barrier acknowledgement. PlayerId={PlayerId}, Epoch={Epoch}", receivedFrom, message?.Epoch);
                }
            });
        }

        protected override bool HasControlOverUI => true;

        public MultiplayerHost(
            ILogger<MultiplayerHost> logger,
            IGameInteractionService gameInteractionService,
            ILevelingInteractionService levelingInteractionService,
            IPlayerNotificationService playerNotificationService,
            IDialogInteractionService dialogInteractionService,
            IGlobalMapInteractionService globalMapInteractionService,
            IPingInteractionService pingInteractionService,
            ICombatInteractionService combatInteractionService,
            IMultiplayerSettingsService multiplayerSettingsProvider,
            IFileSystemService fileSystemService,
            INetworkHostConnection networkHost,
            IValueGenerator valueGenerator,
            IHashService hashService,
            IMapper mapper,
            IMainThreadAccessor mainThreadAccessor)
            : base(logger,
                  mapper,
                  multiplayerSettingsProvider,
                  gameInteractionService,
                  levelingInteractionService,
                  playerNotificationService,
                  dialogInteractionService,
                  globalMapInteractionService,
                  pingInteractionService,
                  combatInteractionService,
                  fileSystemService,
                  valueGenerator,
                  networkHost)
        {
            _networkHost = networkHost;
            _hashService = hashService;
            _mainThreadAccessor = mainThreadAccessor;

            SetupNetworkMessageHandlers();
        }

        public void Create(string gameId, string gamePassword, Entities.Connectivity.ExternalServer externalServer, NetworkGameStartUp gameStartUp)
        {
            _realTimeRuleRolls.Reset();
            _realTimeEntityIds.Reset();
            _realTimeCriticalFaulted = false;
            if (_networkHost.IsActive)
            {
                _networkHost.Reset();
            }

            Game = new NetworkGame(gameStartUp)
            {
                LocalPlayerId = NetworkConstants.HostPlayerId,
                Id = gameId,
                SessionSeed = CreateRandomSeed()
            };

            Game.Characters.AddRange(gameStartUp.Characters);
            lock (_realTimeCommandLock)
            {
                _realTimeCommandGate = null;
                _realTimeCommandGateState = null;
                _realTimeLocalCommandEpoch = 0;
                _nextRealTimeLocalCommandSequence = 0;
            }
            lock (_realTimePauseLock)
            {
                _lastRealTimePauseRequestByPlayer.Clear();
                _realTimePauseRequestEpoch = 0;
                _nextLocalRealTimePauseRequestSequence = 0;
            }

            var settings = SettingsService.GetSettings();

            var serverConfiguration = Mapper.Map<NetworkServerConfiguration>(settings);
            _networkHost.HostTcpServer(serverConfiguration);

            if (externalServer != null)
            {
                var externalServerConfiguration = new ExternalServerConfiguration
                {
                    Password = gamePassword,
                    AutoCreateGame = true,
                    Port = settings.PeerToPeerPort,
                    Server = Mapper.Map<Networking.Configuration.ExternalServer>(externalServer)
                };
                _networkHost.EnableExternalConnections(externalServerConfiguration);
            }

            OnCharactersChanged?.Invoke(Game.StartUp.Title, Game.Characters);
            Logger.LogInformation("Host has been created. GameId={GameId}, IsNewGameSequence={IsNewGameSequence}, SavePath={SavePath}", Game.Id, gameStartUp.IsNewGameSequence, gameStartUp.SavePath);
        }

        public void ChangeHostedStartingPoint(string gameId, NetworkGameStartUp gameStartUp)
        {
            Game.Id = gameId;
            Game.StartUp = gameStartUp;
            Game.Characters.Clear();
            Game.Characters.AddRange(gameStartUp.Characters);

            ResetCharacterOwnership();
            var charactersChanged = new NotifyLobbyCharactersChanged
            {
                Title = Game.StartUp.Title,
                Characters = Mapper.Map<List<Networking.Messages.Contracts.NetworkCharacter>>(Game.Characters)
            };
            Send(charactersChanged);

            OnCharactersChanged?.Invoke(Game.StartUp.Title, Game.Characters);
            Logger.LogInformation("Game starting point has been updated. GameId={GameId}, IsNewGameSequence={IsNewGameSequence}, SavePath={SavePath}", Game.Id, Game.StartUp.IsNewGameSequence, Game.StartUp.SavePath);
        }

        public void ChangeCharacterOwner(NetworkCharacter character, NetworkPlayer player)
        {
            lock (ActionLock)
            {
                var actualPlayer = GetPlayer(player.Id);
                if (actualPlayer == null)
                {
                    Logger.LogWarning("Unable to change character owner for missed player. PlayerId={PlayerId}", player.Id);
                    return;
                }

                var actualCharacter = FindCharacter(character);
                if (actualCharacter.Owner?.Id == actualPlayer.Id)
                {
                    return;
                }

                actualCharacter.Owner = actualPlayer;
                Logger.LogInformation("New character owner. CharacterName={CharacterName}, PlayerId={PlayerId}", actualCharacter.Name, actualPlayer.Id);

                // UnitId becomes relevant once we are in the game
                if (!string.IsNullOrEmpty(actualCharacter.UnitId))
                {
                    UpdateCharacterOwnershipHistory(actualCharacter);
                }

                var characterOwnerChanged = new NotifyCharacterOwnerChanged
                {
                    Character = Mapper.Map<Networking.Messages.Contracts.NetworkCharacter>(actualCharacter)
                };
                Send(characterOwnerChanged);

                UpdateInGameCharacterOwnershipChange(actualCharacter);
            }
        }

        public void Reset()
        {
            Logger.LogInformation("Resetting");
            _realTimeRuleRolls.Reset();
            _realTimeEntityIds.Reset();
            _realTimeCriticalFaulted = false;
            Game = null;
            _realTimeSnapshotHashes.Clear();
            _realTimeSnapshotSentAt.Clear();
            _consecutiveMissingRealTimeReplies = 0;
            _lastRealTimeModePrepare = null;
            _lastRealTimeModeCommit = null;
            _lastRealTimeModeComplete = null;
            _lastRealTimeAbort = null;
            _realTimeModeSwitchDeadline = default;
            _realTimeRepairStage = null;
            _lastRealTimeRepairMessage = null;
            _realTimeRepairDeadline = default;

            _networkHost.Reset();
        }

        public bool Start()
        {
            var hostContent = Game.Players.FirstOrDefault(p => p.IsHost)?.ContentState;
            var incompatiblePlayer = Game.Players.FirstOrDefault(p =>
                p.ContentState.RealTimeProtocolVersion != NetworkRealTimeCombatState.ProtocolVersion
                || (hostContent != null && !string.Equals(p.ContentState.GameVersion, hostContent.GameVersion, StringComparison.OrdinalIgnoreCase))
                || p.ContentState.DiscrepantDLCs.Any()
                || p.ContentState.DiscrepantMods.Any());
            if (incompatiblePlayer != null)
            {
                Logger.LogWarning(
                    "Cannot start lobby: incompatible content. PlayerId={PlayerId}, HostGameVersion={HostGameVersion}, ClientGameVersion={ClientGameVersion}, HostProtocol={HostProtocol}, ClientProtocol={ClientProtocol}, DLCs={DLCs}, Mods={Mods}",
                    incompatiblePlayer.Id,
                    hostContent?.GameVersion,
                    incompatiblePlayer.ContentState.GameVersion,
                    NetworkRealTimeCombatState.ProtocolVersion,
                    incompatiblePlayer.ContentState.RealTimeProtocolVersion,
                    string.Join(", ", incompatiblePlayer.ContentState.DiscrepantDLCs.Select(x => $"{x.DLC?.Id}:{x.Reason}")),
                    string.Join(", ", incompatiblePlayer.ContentState.DiscrepantMods.Select(x => $"{x.Id}:{x.Reason}:host={x.HostVersion}:client={x.Version}")));
                PlayerNotification.ShowModalMessage(WellKnownKeys.MultiplayerWindow.ExternalServers.Errors.ModConflict.Key);
                return false;
            }

            Logger.LogInformation("Starting hosted game...");
            SetLobbyStage(NetworkLobbyStage.PreparingToStart);
            Game.LoadedSaveSeed = CreateRandomSeed();

            var host = GetHost();
            UpdateLobbySyncStatus(host, NetworkLobbySyncStatus.Succeed);

            var saveSyncStatusChanged = new NotifySaveGameSyncStatusChanged
            {
                Status = host.LobbySyncStatus.ToString(),
                PlayerId = host.Id,
            };
            Send(saveSyncStatusChanged);

            try
            {
                TransferSaveGame(requiresProgress: true);
            }
            catch (Exception ex)
            {
                PlayerNotification.ShowModalMessage(WellKnownKeys.SysMessages.FailedToTransferSave.Key, args: ex.Message);
                Logger.LogError(ex, "Error while transferring save file");
                throw;
            }

            TryStartSavedGame();

            return true;
        }

        public override void OnAreaLoaded()
        {
            base.OnAreaLoaded();

            var areaSeed = CreateRandomSeed();
            SetAreaSeed(areaSeed);

            TryEndForcedPause(Game.LocalPlayerId);
        }

        public void OnAreaTransition(NetworkAreaTransition areaTransition)
        {
            // make sure next area load is not affected by last combat turn seed
            ResetLastCombatTurn();

            var message = new NotifyPartyAreaTransitioned
            {
                Transition = Mapper.Map<Networking.Messages.Contracts.NetworkAreaTransition>(areaTransition)
            };
            Send(message);
        }

        public void OnAfterCueShow(NetworkDialog networkDialog, string cueName, bool hasSystemAnswer)
        {
            Logger.LogInformation("Showing dialog Cue. DialogId={DialogId}, DialogName={DialogName}, CueName={CueName}, HasSystemAnswer={HasSystemAnswer}", networkDialog.Id, networkDialog.Name, cueName, hasSystemAnswer);
            if (hasSystemAnswer)
            {
                DialogInteraction.SetDialogContinueButtonState(false);
            }

            if (Game.DialogState == null)
            {
                Logger.LogWarning("Showing dialog cue for not initialized dialog. Most likely it is an autosave load with auto-started dialog after rest. Reinitializing dialog...");
                Game.DialogState = new NetworkDialogState(networkDialog);
            }

            Game.DialogState.CurrentCueName = cueName;
            AddCueWitness(cueName, Game.LocalPlayerId);

            TryEnableDialogContinueButton();
        }

        public bool OnBeforeSelectDialogAnswer(NetworkDialog networkDialog, string cueName, string answerName, bool isExitAnswer, string manualUnitSelectionId)
        {
            Logger.LogInformation("Select Dialog Answer. DialogId={DialogId}, DialogName={DialogName}, CueName={CueName} Answer={Answer}, IsExitAnswer={IsExitAnswer}, ManualUnitSelectionId={ManualUnitSelectionId}",
                networkDialog.Id, networkDialog.Name, cueName, answerName, isExitAnswer, manualUnitSelectionId);

            var missingPlayers = GetPlayersWhoHaveNotSeenCueYet(cueName);
            if (missingPlayers.Count > 0)
            {
                var names = string.Join(";", missingPlayers.Select(p => p.Name));
                Logger.LogWarning("Some players haven't seen the dialog yet. Players={Players}", names);
                DialogInteraction.PlayUnableToSelectCueAnimation(answerName);
                PlayerNotification.ShowWarningNotification(WellKnownKeys.GameNotifications.Dialogs.WaitingForOtherPlayers.Key, addToLog: false);
                return false;
            }

            DialogInteraction.ResetSuggestedDialogAnswers();
            Game.DialogState.AnswerSuggestions.Clear();
            Game.DialogState.CueViews.TryRemove(cueName, out _);
            Game.DialogState.Answer = new NetworkDialogAnswer
            {
                AnswerName = answerName,
                CueName = cueName,
                ManualUnitSelectionId = manualUnitSelectionId
            };

            /// the game performs its "dialog stat check" roll logic a bit later,
            /// so the answer can't be sent right away unless it's the last one
            if (isExitAnswer)
            {
                SendSelectedAnswer();
            }

            return true;
        }

        public void MakeCueAnswerSuggestion(string cueName, string answerName)
        {
            if (Game.DialogState == null
                || !string.Equals(Game.DialogState.CurrentCueName, cueName, StringComparison.OrdinalIgnoreCase)
                || Game.DialogState.IsSelectingAnswer
                || GetPlayersWhoHaveNotSeenCueYet(Game.DialogState.CurrentCueName).Count > 0)
            {
                Logger.LogWarning("Invalid dialog state to make answer suggestions");
                return;
            }

            UpdateCueAnswerSuggestions(Game.LocalPlayerId, cueName, answerName);
        }

        public void SendSelectedAnswer()
        {
            if (Game.DialogState == null)
            {
                Logger.LogError("Unable to send dialog answer because dialog is null");
                return;
            }

            if (Game.DialogState.Answer == null)
            {
                return;
            }

            var message = new NotifyDialogCueAnswerSelected
            {
                Dialog = Mapper.Map<Networking.Messages.Contracts.NetworkDialog>(Game.DialogState.Dialog),
                CueName = Game.DialogState.Answer.CueName,
                AnswerName = Game.DialogState.Answer.AnswerName,
                ManualUnitSelectionId = Game.DialogState.Answer.ManualUnitSelectionId
            };

            Send(message);
            Game.DialogState.Answer = null;
        }

        public bool StartDialog(NetworkDialog networkDialog)
        {
            if (!string.Equals(Game.DialogState?.Dialog.Id, networkDialog.Id, StringComparison.OrdinalIgnoreCase))
            {
                var previousDialog = Game.DialogState;
                Game.DialogState = new NetworkDialogState(networkDialog);
                lock (ActionLock)
                {
                    // workaround for scripted sequential dialogs (e.g. Act4 Shamira -> Nocticula message dialog)
                    if (networkDialog.IsScripted && previousDialog != null)
                    {
                        Logger.LogWarning("Previous cue views have been preserved. PreviousDialogName={PreviousDialogName}, NewDialogName={NewDialogName}", previousDialog.Dialog.Name, Game.DialogState.Dialog.Name);
                        Game.DialogState.CueViews = previousDialog.CueViews ?? [];
                    }
                }
            }

            if (!Game.DialogState.Dialog.IsScripted)
            {
                var message = new NotifyDialogStarted
                {
                    Dialog = Mapper.Map<Networking.Messages.Contracts.NetworkDialog>(networkDialog)
                };
                Send(message);
            }

            if (Game.Combat == null)
            {
                ResetLastCombatTurn();
            }

            Logger.LogInformation("Dialog has been started. DialogId={DialogId}, DialogName={Name}, IsScripted={IsScripted}", Game.DialogState.Dialog.Id, Game.DialogState.Dialog.Name, Game.DialogState.Dialog.IsScripted);
            ResetManualPause();
            return true;
        }

        /// <summary>
        /// 35 - UnitCombatPrepareController
        /// </summary>
        /// <returns></returns>
        public bool CanInitializeCombat()
        {
            try
            {
                if (Game.Combat == null)
                {
                    return false;
                }

                if (Game.CurrentArea == null || Game.CurrentArea.Seed == 0)
                {
                    return false;
                }

                switch (Game.Combat.Stage)
                {
                    case NetworkCombatStage.Initiating:
                        if (Game.Combat.IsInitiated)
                        {
                            var canAdvanceStage = Game.Combat.PlayersInCombat.Count >= GetSyncedPlayersCount();
                            if (canAdvanceStage)
                            {
                                Game.Combat.PlayersInCombat.Clear();
                                SetCombatStage(NetworkCombatStage.Collecting);
                            }

                            return false;
                        }

                        Game.Combat.PlayersInCombat.TryAdd(Game.LocalPlayerId, true);
                        var initialUnits = CombatInteraction.GetUnitsInCombat();
                        var initiatedMessage = new NotifyCombatInitiated
                        {
                            PlayerId = Game.LocalPlayerId,
                            InitialUnits = [.. initialUnits.Select(x => x.Id)]
                        };
                        Send(initiatedMessage);
                        Game.Combat.IsInitiated = true;
                        return false;
                    case NetworkCombatStage.Collecting:
                        if (CombatInteraction.AreThereAnyProjectilesLaunchedByParty())
                        {
                            return false;
                        }

                        if (Game.Combat.IsDataCollected)
                        {
                            var canEndCollectingStage = Game.Combat.InitialUnitsInCombat.Count >= GetSyncedPlayersCount();
                            if (canEndCollectingStage)
                            {
                                SetCombatStage(NetworkCombatStage.Reconciling);
                            }
                            return false;
                        }

                        var unitsInCombat = CombatInteraction.GetUnitsInCombat();
                        Game.Combat.InitialUnitsInCombat.TryAdd(Game.LocalPlayerId, [.. unitsInCombat.Select(x => x.Id)]);
                        Game.Combat.IsDataCollected = true;

                        return false;
                    case NetworkCombatStage.Reconciling:
                        if (Game.Combat.IsDataCompared)
                        {
                            var canEndComparingStage = Game.Combat.UnavailableUnits.Count >= GetSyncedPlayersCount();
                            if (canEndComparingStage)
                            {
                                SetCombatStage(NetworkCombatStage.Resyncing);
                            }

                            return false;
                        }

                        var discrepantUnits = GeCombatUnitsDiscrepancy();
                        foreach (var (player, units) in discrepantUnits)
                        {
                            if (units.Count == 0)
                            {
                                Game.Combat.UnavailableUnits.TryAdd(player, []);
                                continue;
                            }

                            if (player == Game.LocalPlayerId)
                            {
                                CombatInteraction.EnsureUnitsInCombatAsync(units)
                                    .ContinueWith(x => Game.Combat.UnavailableUnits.TryAdd(Game.LocalPlayerId, x.Result));
                                continue;
                            }

                            var discrepancyDetected = new NotifyCombatDataDiscrepancyDetected
                            {
                                Units = units
                            };
                            Send(player, discrepancyDetected);
                        }

                        Game.Combat.IsDataCompared = true;
                        return false;
                    case NetworkCombatStage.Resyncing:
                        if (Game.Combat.IsSynced)
                        {
                            var canEndSyncingStage = Game.Combat.UnavailableUnits.Count == 0;
                            if (canEndSyncingStage)
                            {
                                Game.Combat.Seed = CreateRandomSeed();
                                SetCombatStage(NetworkCombatStage.Finalizing);
                            }

                            return false;
                        }

                        var unitsToRemoveFromCombat = Game.Combat.UnavailableUnits.Values.SelectMany(x => x).Distinct().ToList();
                        var message = new NotifyCombatUnitsExcluded
                        {
                            Units = unitsToRemoveFromCombat
                        };
                        Send(message);
                        CombatInteraction.ExcludeUnitsFromCombatAsync(unitsToRemoveFromCombat)
                            .ContinueWith(x => Game.Combat.UnavailableUnits.TryRemove(Game.LocalPlayerId, out _));

                        Game.Combat.IsSynced = true;
                        return false;
                    case NetworkCombatStage.Finalizing:
                    case NetworkCombatStage.Running:
                        return true;
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error while initializing combat");
                throw;
            }
        }

        /// <summary>
        /// 12 - CombatController
        /// </summary>
        /// <returns></returns>
        public bool CanContinueCombat()
        {
            try
            {
                if (Game.Combat == null)
                {
                    return false;
                }

                switch (Game.Combat.Stage)
                {
                    case NetworkCombatStage.Finalizing:
                        if (!CombatInteraction.IsCombatInitialized())
                        {
                            return false;
                        }

                        if (Game.Combat.IsStarted)
                        {
                            var canContinue = Game.Combat.PlayersFinishedStartupSequence.Count >= GetSyncedPlayersCount();
                            if (canContinue)
                            {
                                SetCombatStage(NetworkCombatStage.Running);
                            }
                            return false;
                        }

                        var combatState = CombatInteraction.GetCombatState();
                        var message = new NotifyCombatInitializationRequired
                        {
                            State = Mapper.Map<Networking.Messages.Contracts.NetworkCombatState>(combatState),
                            CombatSeed = Game.Combat.Seed,
                            TriggeredAreaEffects = Mapper.Map<List<Networking.Messages.Contracts.NetworkAreaEffect>>(Game.Combat.TriggeredAreaEffects)
                        };
                        Game.Combat.TriggeredAreaEffects.Clear();
                        Send(message);

                        Game.Combat.IsStarted = true;
                        Game.Combat.PlayersFinishedStartupSequence.TryAdd(Game.LocalPlayerId, true);

                        return false;
                    case NetworkCombatStage.Running:
                        return true;
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error while continuing combat");
                throw;
            }
        }

        public void OnPerceptionCheck(NetworkPerceptionCheck check)
        {
            var message = new NotifyPerceptionCheckRolled
            {
                Check = Mapper.Map<Networking.Messages.Contracts.NetworkPerceptionCheck>(check)
            };
            Send(message);
        }

        public void OnInspectionKnowledgeCheck(NetworkInspectionKnowledgeCheck check)
        {
            var message = new NotifyInspectionKnowledgeCheckRolled
            {
                Check = Mapper.Map<Networking.Messages.Contracts.NetworkInspectionKnowledgeCheck>(check)
            };
            Send(message);
        }

        public void OnStealthPerceptionCheckRolled(NetworkStealthPerceptionCheck check)
        {
            var message = new NotifyStealthPerceptionCheckRolled
            {
                Check = Mapper.Map<Networking.Messages.Contracts.NetworkStealthPerceptionCheck>(check)
            };
            Send(message);
        }

        public bool OnSpawnCampPlace(NetworkVector3 position)
        {
            var message = new NotifySpawnCampPlace
            {
                Position = Mapper.Map<Networking.Messages.Contracts.NetworkVector3>(position)
            };
            Send(message);

            return true;
        }

        public void OnCampingUseHealingSpellsChanged(bool isOn)
        {
            var message = new NotifyCampingUseHealingSpellsChanged { IsOn = isOn };
            Send(message);
        }

        public void OnCampingStateChanged(NetworkCampingState state)
        {
            var message = new NotifyCampingStateChanged
            {
                State = Mapper.Map<Networking.Messages.Contracts.NetworkCampingState>(state)
            };
            Send(message);
        }

        public void OnCampingUnitsRoleChanged(List<NetworkCampingRole> roles)
        {
            var message = new NotifyCampingUnitsRoleChanged
            {
                Roles = Mapper.Map<List<Networking.Messages.Contracts.NetworkCampingRole>>(roles),
            };
            Send(message);
        }

        public void OnAfterTryRollRestRandomEncounter(NetworkRandomEncounterContext encounterContext)
        {
            try
            {
                if (encounterContext == null)
                {
                    Logger.LogError("Rest random encounter rolling is finished, but context has not been recorded");
                    return;
                }

                Game.Rest.RandomEncounters.Add(encounterContext.Recording);
                Logger.LogInformation("Rest random encounter context has been stored. SleepPhase={SleepPhase}, Data={Data}", Game.Rest.SleepPhase, encounterContext.Recording);

                if (encounterContext.Recording.RandomUnitSeed.HasValue)
                {
                    var settings = SettingsService.GetSettings();
                    lock (ActionLock)
                    {
                        EnsureForcePaused(NetworkForcedPauseReason.RestEncounterLoading, settings.ForcedPauseRandomEncounterTerminationDelay);
                    }

                    GameInteraction.SetPause(true);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to store rest random encounter context");
                throw;
            }
        }

        public void OnMakeVendorDeal()
        {
            var message = new NotifyVendorDealMade();
            Send(message);
        }

        public void OnCloseVendorWindow()
        {
            var message = new NotifyVendorWindowClosed();
            Send(message);
        }

        public bool OnRequestLevelingUI(string unitId, NetworkLevelingType levelingType)
        {
            lock (ActionLock)
            {
                if (Game.Leveling == null)
                {
                    InitiateLeveling(unitId, levelingType);
                    SendLevelingStartedConfirmation();
                    return true;
                }

                Logger.LogWarning("New leveling has been denied as another one is progress. UnitId={UnitId}, Type={Type}", Game.Leveling.UnitId, Game.Leveling.Type);
                return false;
            }
        }

        public void OnAutoPausedByTrapDetection()
        {
            lock (ActionLock)
            {
                EnsureForcePaused(NetworkForcedPauseReason.TrapDetected, removalDelay: null);
                Game.ForcedPause.ReadyPlayers.Add(Game.LocalPlayerId);
            }
        }

        public void OnCloseGroupChangerPartyUI()
        {
            ResetPlayersTracker(Game.PlayersInGroupChanger);

            var message = new NotifyGroupChangerClosed();
            Send(message);
        }

        public void OnDialogPopupAccepted(NetworkDialogPopup networkDialogPopup)
        {
            ResetPlayersTracker(Game.PlayersInDialogPopup);
            var message = new NotifyDialogPopupAccepted
            {
                Popup = Mapper.Map<Networking.Messages.Contracts.NetworkDialogPopup>(networkDialogPopup)
            };
            Send(message);
        }

        public void OnDialogPopupClosed(NetworkDialogPopup networkDialogPopup)
        {
            ResetPlayersTracker(Game.PlayersInDialogPopup);
            var message = new NotifyDialogPopupClosed
            {
                Popup = Mapper.Map<Networking.Messages.Contracts.NetworkDialogPopup>(networkDialogPopup)
            };
            Send(message);
        }

        public bool OnClickGroupChangerUnit(string unitId)
        {
            var everyoneIsReady = false;
            lock (ActionLock)
            {
                everyoneIsReady = Game.PlayersInGroupChanger.Count >= GetSyncedPlayersCount();
            }

            if (!everyoneIsReady)
            {
                return everyoneIsReady;
            }

            var message = new NotifyGroupChangerUnitClicked
            {
                UnitId = unitId
            };
            Send(message);

            return true;
        }

        public void OnAcceptGroupChangerParty()
        {
            var message = new NotifyGroupChangerPartyAccepted();
            Send(message);
            ResetPlayersTracker(Game.PlayersInGroupChanger);
        }

        public void OnGlobalMapRestOpened()
        {
            var message = new NotifyGlobalMapRestOpened();
            Send(message);
        }

        public void OnRestWindowClosed()
        {
            var message = new NotifyRestWindowClosed();
            Send(message);
        }

        public void OnGlobalMapGroupChangerOpened()
        {
            var message = new NotifyGlobalMapGroupChangerOpened();
            Send(message);
        }

        public void OnGlobalMapTravelStarted(NetworkGlobalMapTravel globalMapTravel)
        {
            var party = CombatInteraction.GetParty();
            var message = new NotifyGlobalMapTravelStarted
            {
                Travel = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapTravel>(globalMapTravel),
                Party = Mapper.Map<List<Networking.Messages.Contracts.NetworkUnit>>(party)
            };
            Send(message);
        }

        public void OnGlobalMapSelectedArmyChanged(NetworkGlobalMapArmy globalMapArmy)
        {
            var message = new NotifyGlobalMapSelectedArmyChanged
            {
                Army = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapArmy>(globalMapArmy)
            };
            Send(message);
        }

        public void OnGlobalMapAutoCrusadeCombatChanged(bool isEnabled)
        {
            var message = new NotifyGlobalMapAutoCrusadeCombatChanged
            {
                IsEnabled = isEnabled
            };
            Send(message);
        }

        public void OnSkipTimeClosed()
        {
            ResetPlayersTracker(Game.PlayersInSkipTime);

            var message = new NotifySkipTimeClosed();
            Send(message);
        }

        public void OnSkipTimeHoursChanged(float hours)
        {
            var message = new NotifySkipTimeHoursChanged
            {
                Hours = hours
            };
            Send(message);
        }

        public void OnSkipTimeStarted()
        {
            var message = new NotifySkipTimeStarted();
            Send(message);
        }

        public bool OnGlobalMapSelectLocation(NetworkGlobalMapLocation globalMapLocation)
        {
            var readyPlayers = GetPlayersCountWithSyncedGlobalMapMode();
            if (!readyPlayers.HasValue)
            {
                Logger.LogError("Global Map location select has been denied due to invalid ready players value");
                return false;
            }

            var canSelect = readyPlayers.Value >= GetSyncedPlayersCount();
            if (!canSelect)
            {
                PlayerNotification.ShowWarningNotification(WellKnownKeys.GameNotifications.ForcedPause.AreaLoading.Key);
            }

            return canSelect;
        }

        public void OnGlobalMapContinueTravel(NetworkGlobalMapTraveler globalMapTraveler)
        {
            var party = CombatInteraction.GetParty();
            var message = new NotifyGlobalMapTravelContinued
            {
                Traveler = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapTraveler>(globalMapTraveler),
                Party = Mapper.Map<List<Networking.Messages.Contracts.NetworkUnit>>(party)
            };
            Send(message);
        }

        public void OnGlobalMapStopTravel(NetworkGlobalMapTraveler globalMapTraveler)
        {
            var party = CombatInteraction.GetParty();
            var message = new NotifyGlobalMapTravelStopped
            {
                Traveler = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapTraveler>(globalMapTraveler),
                Party = Mapper.Map<List<Networking.Messages.Contracts.NetworkUnit>>(party)
            };
            Send(message);
        }

        public void OnGlobalMapCommonPopupAccepted(NetworkGlobalMapCommonPopup globalMapCommonPopup)
        {
            var message = new NotifyGlobalMapCommonPopupAccepted
            {
                Popup = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapCommonPopup>(globalMapCommonPopup)
            };
            Send(message);
        }

        public void OnGlobalMapEncounterAccepted()
        {
            var message = new NotifyGlobalMapEncounterAccepted();
            Send(message);
        }

        public void OnGlobalMapEncounterAvoided()
        {
            var message = new NotifyGlobalMapEncounterAvoided();
            Send(message);
        }

        public void OnGlobalMapRandomEncounterRolled(NetworkGlobalMapEncounter globalMapEncounter)
        {
            var message = new NotifyGlobalMapEncounterRolled
            {
                Encounter = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapEncounter>(globalMapEncounter)
            };
            Send(message);
        }

        public void OnGlobalMapSkipDay()
        {
            var message = new NotifyGlobalMapDaySkipped();
            Send(message);
        }

        public void OnZoneLootRemoveToggleChanged(bool removeUncollectedLoot)
        {
            var message = new NotifyZoneLootRemoveToggleChanged
            {
                RemoveLoot = removeUncollectedLoot
            };
            Send(message);
        }

        public void OnZoneLootCompleted()
        {
            var message = new NotifyZoneLootCompleted();
            Send(message);
        }

        public void OnZoneLootLeft()
        {
            var message = new NotifyZoneLootLeft();
            Send(message);
        }

        public void OnCharacterSelectionWindowAccepted()
        {
            ResetPlayersTracker(Game.PlayersInCharacterSelectionWindow);

            var message = new NotifyCharacterSelectionWindowAccepted();
            Send(message);
        }

        public void OnCharacterSelectionWindowClosed()
        {
            ResetPlayersTracker(Game.PlayersInCharacterSelectionWindow);

            var message = new NotifyCharacterSelectionWindowClosed();
            Send(message);
        }

        public void OnCharacterSelectionToggleChanged(string unitId)
        {
            var message = new NotifyCharacterSelectionToggleChanged
            {
                UnitId = unitId
            };
            Send(message);
        }

        public void OnNewGameDifficultyChanged(string difficulty)
        {
            var message = new NotifyNewGameDifficultyChanged
            {
                Difficulty = difficulty
            };
            Send(message);
        }

        public bool OnCreateAndEquipPolymorphInSlot(NetworkPolymorphicItem polymorphicItem)
        {
            var isInParty = IsControlledByPlayers(polymorphicItem.UnitId) || GameInteraction.IsUnitInParty(polymorphicItem.UnitId);
            if (!isInParty)
            {
                return true;
            }

            var message = new NotifyPolymorphicItemCreated
            {
                PolymorphicItem = Mapper.Map<Networking.Messages.Contracts.NetworkPolymorphicItem>(polymorphicItem)
            };
            Send(message);

            return true;
        }

        public void OnCrusadeArmyBattleResultsClosed()
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmyBattleResults);
            var message = new NotifyCrusadeArmyBattleResultsClosed();
            Send(message);
        }

        public void OnCrusadeArmyBattleResultsManualCombatStarted()
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmyBattleResults);
            var message = new NotifyCrusadeArmyBattleResultsManualCombatStarted();
            Send(message);
        }

        public void OnGlobalMapLocationMessageAccepted()
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapLocationMessage);
            var message = new NotifyGlobalMapLocationMessageAccepted();
            Send(message);
        }

        public void OnGlobalMapLocationMessageClosed()
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapLocationMessage);

            var message = new NotifyGlobalMapLocationMessageClosed
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);

            UpdateGlobalMapLocationMessageUIState();
        }

        public void OnGlobalMapCommonPopupDeclined(NetworkGlobalMapCommonPopup globalMapCommonPopup)
        {
            RemovePlayerFromTracker(Game.PlayersInGlobalMapCommonPopup, Game.LocalPlayerId);

            var message = new NotifyGlobalMapCommonPopupDeclined
            {
                PlayerId = Game.LocalPlayerId,
                Popup = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapCommonPopup>(globalMapCommonPopup)
            };
            Send(message);
        }

        public void OnGlobalMapCombatResultsClosed()
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapCombatResults);
            var message = new NotifyGlobalMapCombatResultsClosed();
            Send(message);
        }

        public bool OnTacticalCombatInitialization()
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapCombatResults);

            Game.ArmyCombat = new NetworkArmyCombat
            {
                IsInitialized = false,
                Seed = CreateRandomSeed()
            };
            return true;
        }

        public void OnTacticalCombatInitialized()
        {
            Game.ArmyCombat.PlayersCombatInitialization.TryAdd(Game.LocalPlayerId, true);
            Game.ArmyCombat.IsInitialized = TryConfirmTacticalCombatInitialization();
            Game.ArmyCombat.AreaSeed = CombatInteraction.GetCrusadeArmyCombatAreaSeed();

            var message = new NotifyTacticalCombatInitialized()
            {
                AreaSeed = Game.ArmyCombat.AreaSeed,
                Seed = Game.ArmyCombat.Seed
            };
            Send(message);
        }

        public void OnTacticalCombatUnitUseAbilityCommand(NetworkTacticalUnitUseAbilityCommand tacticalUnitUseAbilityCommand)
        {
            var message = new NotifyTacticalUnitUseAbilityCommandExecuted
            {
                Command = Mapper.Map<Networking.Messages.Contracts.NetworkTacticalUnitUseAbilityCommand>(tacticalUnitUseAbilityCommand)
            };
            Send(message);
        }

        public void OnTacticalCombatAccelerationChanged(bool isAccelerated)
        {
            var message = new NotifyTacticalCombatAccelerationChanged
            {
                IsAccelerated = isAccelerated
            };
            Send(message);
        }

        public void OnTacticalCombatUnitAttackCommand(NetworkTacticalUnitAttackCommand tacticalUnitAttackCommand)
        {
            var message = new NotifyTacticalUnitAttackCommandExecuted
            {
                Command = Mapper.Map<Networking.Messages.Contracts.NetworkTacticalUnitAttackCommand>(tacticalUnitAttackCommand)
            };
            Send(message);
        }

        public void OnTacticalCombatUnitMoveToCommand(NetworkTacticalUnitMoveToCommand tacticalUnitMoveToCommand)
        {
            var message = new NotifyTacticalUnitMoveToCommandExecuted
            {
                Command = Mapper.Map<Networking.Messages.Contracts.NetworkTacticalUnitMoveToCommand>(tacticalUnitMoveToCommand)
            };
            Send(message);
        }

        public bool OnTacticalCombatTotalDefenseUsed()
        {
            var message = new NotifyTacticalCombatTotalDefenseUsed();
            Send(message);
            return true;
        }

        public bool OnTacticalCombatTurnPostponed()
        {
            var message = new NotifyTacticalCombatTurnPostponed();
            Send(message);
            return true;
        }

        public void OnTacticalCombatRetreat()
        {
            var message = new NotifyTacticalCombatRetreated();
            Send(message);
        }

        public void OnGlobalMapCrusadeArmyInfoClosed()
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmyInfo);

            var message = new NotifyGlobalMapCrusadeArmyInfoClosed();
            Send(message);
        }

        public void OnGlobalMapCrusadeArmyMoveSquadsToMainArmy()
        {
            var message = new NotifyGlobalMapCrusadeArmySquadsMovedToMainArmy();
            Send(message);
        }

        public void OnGlobalMapCrusadeArmyMoveSquadsToSecondArmy()
        {
            var message = new NotifyGlobalMapCrusadeArmySquadsMovedToSecondArmy();
            Send(message);
        }

        public void OnGlobalMapCrusadeArmyInfoNextMergeArmy()
        {
            var message = new NotifyGlobalMapCrusadeArmyInfoNextMergeArmySelected();
            Send(message);
        }

        public void OnGlobalMapCrusadeArmyInfoPrevMergeArmy()
        {
            var message = new NotifyGlobalMapCrusadeArmyInfoPrevMergeArmySelected();
            Send(message);
        }

        public void OnGlobalMapCrusadeArmyLeaderAction(NetworkGlobalMapArmyLeader globalMapArmyLeader, NetworkGlobalMapArmyLeaderActionType armyLeaderActionType)
        {
            var message = new NotifyGlobalMapCrusadeArmyLeaderActionExecuted()
            {
                Type = armyLeaderActionType.ToString(),
                Leader = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapArmyLeader>(globalMapArmyLeader)
            };
            Send(message);
        }

        public void OnGlobalMapMergeArmies()
        {
            var message = new NotifyGlobalMapCrusadeArmiesMerging();
            Send(message);
        }

        public void OnGlobalMapCreateCrusadeArmy()
        {
            var message = new NotifyGlobalMapCrusadeArmyCreated();
            Send(message);
        }

        public void OnGlobalMapCrusadeArmyMainCartClosed()
        {
            var message = new NotifyGlobalMapCrusadeArmyMainCartClosed();
            Send(message);
        }

        public void OnGlobalMapCrusadeArmyRecruitCartClosed()
        {
            var message = new NotifyGlobalMapCrusadeArmyRecruitCartClosed();
            Send(message);
        }

        public void OnGlobalMapRecruitmentBuyUnits(NetworkGlobalMapUnitRecruitmentOrder globalMapUnitRecruitmentOrder)
        {
            var message = new NotifyGlobalMapUnitsRecruited
            {
                Order = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapUnitRecruitmentOrder>(globalMapUnitRecruitmentOrder)
            };
            Send(message);
        }

        public void OnGlobalMapCrusadeArmyDismiss(NetworkGlobalMapArmy globalMapArmy)
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapCommonPopup);

            var message = new NotifyGlobalMapCrusadeArmyDismissed
            {
                Army = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapArmy>(globalMapArmy),
            };
            Send(message);
        }

        public void OnGlobalMapCrusadeArmyLeaderLevelingClosed()
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmyLeaderLeveling);
            var message = new NotifyGlobalMapCrusadeArmyLeaderLevelingClosed();
            Send(message);
        }

        public void OnGlobalMapCrusadeArmyLeaderLevelingConfirmed()
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmyLeaderLeveling);
            var message = new NotifyGlobalMapCrusadeArmyLeaderLevelingConfirmed();
            Send(message);
        }

        public void OnGlobalMapCrusadeArmyLeaderLevelingSkillSelected(string skillId)
        {
            var message = new NotifyGlobalMapCrusadeArmyLeaderLevelingSkillSelected
            {
                Id = skillId
            };
            Send(message);
        }

        public void OnKingdomEventSelected(NetworkKingdomEvent kingdomEvent)
        {
            var message = new NotifyKingdomEventSelected
            {
                Event = Mapper.Map<Networking.Messages.Contracts.NetworkKingdomEvent>(kingdomEvent)
            };
            Send(message);
        }

        public void OnKingdomEventSolutionSelected(NetworkKingdomEventSolution kingdomEventSolution)
        {
            var message = new NotifyKingdomEventSolutionSelected
            {
                Solution = Mapper.Map<Networking.Messages.Contracts.NetworkKingdomEventSolution>(kingdomEventSolution)
            };
            Send(message);
        }

        public void OnKingdomEventStarted()
        {
            var message = new NotifyKingdomEventStarted();
            Send(message);
        }

        public void OnKingdomEventCancelled()
        {
            var message = new NotifyKingdomEventCancelled();
            Send(message);
        }

        public void OnKingdomEventDropped(NetworkKingdomEvent kingdomEvent)
        {
            var message = new NotifyKingdomEventDropped
            {
                Event = Mapper.Map<Networking.Messages.Contracts.NetworkKingdomEvent>(kingdomEvent)
            };
            Send(message);
        }

        public void OnKingdomUpgradeSettlement(NetworkKingdomSettlement kingdomSettlement)
        {
            var message = new NotifyKingdomSettlementUpgraded
            {
                Settlement = Mapper.Map<Networking.Messages.Contracts.NetworkKingdomSettlement>(kingdomSettlement)
            };
            Send(message);
        }

        public void OnAlushenyrraCameraDirectionChanged(string cameraDirection)
        {
            // TODO: is in isles area?
            if (Game?.CurrentArea == null || Game.CurrentArea.Chapter != 4)
            {
                return;
            }

            var message = new NotifyAlyshenyrraCameraDirectionChanged
            {
                Direction = cameraDirection
            };
            Send(message);
        }

        public void OnNewGameSequenceCampaignChanged(NetworkCampaign campaign)
        {
            var message = new NotifyNewGameSequenceCampaignChanged
            {
                Campaign = Mapper.Map<Networking.Messages.Contracts.NetworkCampaign>(campaign)
            };
            Send(message);
        }

        public void OnNewGameSequenceLastAzlantiChanged(bool isEnabled)
        {
            var message = new NotifyNewGameSequenceLastAzlantiChanged
            {
                IsEnabled = isEnabled
            };
            Send(message);
        }

        public void OnDungeonGameOverGoToMainMenu()
        {
            var message = new NotifyDungeonGameOverTerminated();
            Send(message);
        }

        public void OnDungeonGameOverLoadLatestSave()
        {
            var message = new NotifyDungeonGameOverLastSaveLoaded();
            Send(message);
        }

        public void OnDungeonGameOverStartNewGame()
        {
            var message = new NotifyDungeonGameOverNewGameStarted();
            Send(message);
        }

        public void OnDungeonBoonSelected(NetworkBoon networkBoon)
        {
            var message = new NotifyDungeonBoonSelected
            {
                Boon = Mapper.Map<Networking.Messages.Contracts.NetworkBoon>(networkBoon)
            };
            Send(message);
        }

        public void OnDungeonBoonConfirmed()
        {
            ResetPlayersTracker(Game.PlayersInDungeonBoonSelector);

            var message = new NotifyDungeonBoonConfirmed();
            Send(message);
        }

        public void OnKingdomEnterSettlement(NetworkKingdomSettlement kingdomSettlement, bool requiresUnloadEvent, bool exitSettlementToGlobalMap)
        {
            var message = new NotifyKingdomSettlementEntered
            {
                Settlement = Mapper.Map<Networking.Messages.Contracts.NetworkKingdomSettlement>(kingdomSettlement),
                RequiresUnloadEvent = requiresUnloadEvent,
                ExitSettlementToGlobalMap = exitSettlementToGlobalMap
            };
            Send(message);
        }

        public void OnKingdomLeaveSettlement()
        {
            var message = new NotifyKingdomSettlementLeft();
            Send(message);
        }

        public void OnKingdomSettlementBuildingSold(NetworkKingdomSettlementBuilding kingdomSettlementBuilding)
        {
            var message = new NotifyKingdomSettlementBuildingSold
            {
                Building = Mapper.Map<Networking.Messages.Contracts.NetworkKingdomSettlementBuilding>(kingdomSettlementBuilding)
            };
            Send(message);
        }

        public void OnKingdomSettlementBuilt(NetworkKingdomSettlementBuilding kingdomSettlementBuilding)
        {
            var message = new NotifyKingdomSettlementBuildingBuilt
            {
                Building = Mapper.Map<Networking.Messages.Contracts.NetworkKingdomSettlementBuilding>(kingdomSettlementBuilding)
            };
            Send(message);
        }

        public NetworkAIAction OnAfterAISelectedAction(NetworkAIAction aiAction)
        {
            var message = new NotifyAIActionSelected
            {
                Action = Mapper.Map<Networking.Messages.Contracts.NetworkAIAction>(aiAction)
            };
            if (IsRealTimeCombatActive && Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Stable)
            {
                if (_realTimeAISequenceEpoch != Game.RealTimeCombat.Epoch)
                {
                    _realTimeAISequenceEpoch = Game.RealTimeCombat.Epoch;
                    _nextRealTimeAISequence = 0;
                }
                message.RealTimeEpoch = Game.RealTimeCombat.Epoch;
                message.RealTimeSequence = ++_nextRealTimeAISequence;
            }
            Send(message);
            return null;
        }

        public void OnGlobalMapMagicSpellUsed(NetworkGlobalMapMagicSpell globalMagicSpell)
        {
            var message = new NotifyGlobalMapMagicSpellUsed
            {
                Spell = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapMagicSpell>(globalMagicSpell),
            };
            Send(message);
        }

        public void OnGlobalMapRecruitmentBuyResources(NetworkGlobalMapResourceOrder globalMapResourceOrder)
        {
            var message = new NotifyGlobalMapResourcesBought
            {
                Order = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapResourceOrder>(globalMapResourceOrder)
            };
            Send(message);
        }

        public void OnGlobalMapCrusadeArmyCartNameChanged(NetworkGlobalMapArmy globalMapArmy)
        {
            var message = new NotifyGlobalMapCrusadeArmyInfoCartNameChanged
            {
                Army = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapArmy>(globalMapArmy)
            };
            Send(message);
        }

        public void OnGlobalMapCrusadeArmySetLeaderClear()
        {
            var message = new NotifyGlobalMapCrusadeArmySetLeaderClearClicked();
            Send(message);
        }

        public void OnGlobalMapCrusadeArmySetLeaderRecruit()
        {
            var message = new NotifyGlobalMapCrusadeArmySetLeaderRecruitClicked();
            Send(message);
        }

        public void OnGlobalMapRecruitmentMercReroll()
        {
            var message = new NotifyGlobalMapRecruitmentMercenariesRerolled();
            Send(message);
        }

        public void OnGlobalMapRecruitmentNextArmy()
        {
            var message = new NotifyGlobalMapRecruitmentNextArmySelected();
            Send(message);
        }

        public void OnGlobalMapRecruitmentPrevArmy()
        {
            var message = new NotifyGlobalMapRecruitmentPrevArmySelected();
            Send(message);
        }

        public void OnGlobalMapCrusadeArmySquadDismiss(NetworkGlobalMapArmySquadSlot globalMapArmySquadSlot)
        {
            var message = new NotifyGlobalMapCrusadeArmySquadDismissed
            {
                SquadSlot = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapArmySquadSlot>(globalMapArmySquadSlot),
            };
            Send(message);
        }

        public bool OnGlobalMapCrusadeArmyMergedInOne(NetworkGlobalMapArmySquadSlot globalMapArmySquadSlot)
        {
            var message = new NotifyGlobalMapCrusadeArmyMergedInOne
            {
                SquadSlot = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapArmySquadSlot>(globalMapArmySquadSlot),
            };
            Send(message);
            return true;
        }

        public bool OnGlobalMapCrusadeArmySquadSplit(NetworkGlobalMapArmySquadSlot globalMapArmySquadSlot, int count)
        {
            var message = new NotifyGlobalMapCrusadeArmySquadSplit
            {
                SquadSlot = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapArmySquadSlot>(globalMapArmySquadSlot),
                Count = count
            };
            Send(message);
            return true;
        }

        public void OnGlobalMapCrusadeArmySquadsMerged(NetworkGlobalMapArmySquadSlot sourceSquadSlot, NetworkGlobalMapArmySquadSlot targetSquadSlot, int count)
        {
            var message = new NotifyGlobalMapCrusadeArmySquadsMerged
            {
                SourceSquadSlot = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapArmySquadSlot>(sourceSquadSlot),
                TargetSquadSlot = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapArmySquadSlot>(targetSquadSlot),
                Count = count
            };
            Send(message);
        }

        public void OnGlobalMapCrusadeArmySquadsSwitched(NetworkGlobalMapArmySquadSlot sourceSquadSlot, NetworkGlobalMapArmySquadSlot targetSquadSlot)
        {
            var message = new NotifyGlobalMapCrusadeArmySquadsSwitched
            {
                SourceSquadSlot = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapArmySquadSlot>(sourceSquadSlot),
                TargetSquadSlot = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapArmySquadSlot>(targetSquadSlot),
            };
            Send(message);
        }

        public void OnGlobalMapCrusadeArmySquadSplitRequested(NetworkGlobalMapArmySquadSlot sourceSquadSlot, NetworkGlobalMapArmySquadSlot targetSquadSlot, int count)
        {
            var message = new NotifyGlobalMapCrusadeArmySquadSplitRequested
            {
                SourceSquadSlot = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapArmySquadSlot>(sourceSquadSlot),
                TargetSquadSlot = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapArmySquadSlot>(targetSquadSlot),
                Count = count
            };
            Send(message);
        }

        public override void OnStartRest()
        {
            base.OnStartRest();

            var message = new NotifyRestStarted();
            Send(message);
        }

        public bool OnAreaEffectTriggered(NetworkAreaEffect areaEffect)
        {
            if (Game.Combat != null && Game.Combat.Turn == null && Game.Combat.TriggeredAreaEffects.Add(areaEffect))
            {
                Logger.LogDebug("Area effect has been triggered in combat mid turn. Id={Id}, Name={Name}", areaEffect.Id, areaEffect.Name);
            }

            return true;
        }

        public void OnTransitionMapEntryChosen(string entryId)
        {
            var message = new NotifyTransitionMapEntryChosen
            {
                EntryId = entryId
            };
            Send(message);
        }

        public void OnIslandMapEntryChosen(NetworkIslandMapTransition islandMapTransition)
        {
            var message = new NotifyIslandMapEntryChosen
            {
                Island = Mapper.Map<Networking.Messages.Contracts.NetworkIslandMapTransition>(islandMapTransition)
            };
            Send(message);
        }

        public void OnTransitionMapClosed()
        {
            ResetPlayersTracker(Game.PlayersInTransitionMap);
            var message = new NotifyTransitionMapClosed();
            Send(message);
        }

        public void OnGlobalMapTeleport(NetworkGlobalMapLocation globalMapLocation)
        {
            var message = new NotifyGlobalMapTeleport
            {
                Location = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapLocation>(globalMapLocation)
            };
            Send(message);
        }

        protected override bool OnToggleOffPause(out bool showReason)
        {
            showReason = true;
            return TryEndForcedPause(Game.LocalPlayerId);
        }

        protected override void Send(object message)
        {
            _networkHost.Broadcast(message);
        }

        protected override void Send(long playerId, object message)
        {
            _networkHost.Send(playerId, message);
        }

        protected override void OnCombatStageChanged(NetworkCombatStage combatStage)
        {
            var message = new NotifyCombatStageChanged
            {
                Stage = combatStage.ToString()
            };
            Send(message);
        }

        protected override void OnLocalPlayerTurnStart()
        {
            SetCombatTurnStage(NetworkCombatTurnStage.Starting);
            AddPlayerReadyStatus(PlayerTurnReadinessType.Start, Game.LocalPlayerId, Game.Combat.Turn.UnitId);
            AddPlayerReadyStatus(PlayerTurnReadinessType.UnitSynchronization, Game.LocalPlayerId, Game.Combat.Turn.UnitId);

            TryStartTurn();
        }

        protected override void OnLocalPlayerTurnEnd()
        {
            lock (ActionLock)
            {
                Game.Combat.Turn.PlayersEndTurnInitialization.Add(Game.LocalPlayerId);
                Game.Combat.Turn.PlayersEndTurnSynchronization.Add(Game.LocalPlayerId);
            }

            TryEndTurn();
        }

        protected override bool IsArmyCombatTurnSynced()
        {
            if (Game.ArmyCombat.Turn.IsSyncRequired)
            {
                Game.ArmyCombat.Turn.IsSyncRequired = false;
                Game.ArmyCombat.Turn.SyncedPlayers.Add(Game.LocalPlayerId);

                var units = CombatInteraction.GetUnitsInCombat();
                var message = new NotifyArmyCombatTurnSynchronizationRequired
                {
                    Units = Mapper.Map<List<Networking.Messages.Contracts.NetworkUnit>>(units)
                };
                Send(message);
            }

            var isTurnSynced = Game.ArmyCombat.Turn.SyncedPlayers.Count >= GetSyncedPlayersCount();
            return isTurnSynced;
        }

        protected override void OnLocalRestGameModeEnded()
        {
            base.OnLocalRestGameModeEnded();

            if (Game.ForcedPause != null)
            {
                Game.ForcedPause.ReadyPlayers.Add(Game.LocalPlayerId);
                TryEndForcedPause(Game.LocalPlayerId);
            }
        }

        protected override void OnRemoteRestGameModeEnded(long playerId)
        {
            base.OnRemoteRestGameModeEnded(playerId);

            if (Game.ForcedPause != null)
            {
                lock (ActionLock)
                {
                    Game.ForcedPause.ReadyPlayers.Add(playerId);
                    TryEndForcedPause(Game.LocalPlayerId);
                }
            }
        }

        protected override void SetupNetworkMessageHandlers()
        {
            _networkHost.OnPlayerConnected = OnPlayerConnected;
            _networkHost.OnPlayerDisconnected = OnPlayerDisconnected;
            _networkHost.OnLocalServerStarted = OnLocalServerStarted;
            _networkHost.OnExternalConnectivityUpdated = OnExternalConnectivityUpdated;
            _networkHost.OnError = OnError;

            base.SetupNetworkMessageHandlers();

            _networkHost
               // requests - this is somewhat special because requester is blocking the thread (most likely main game loop) until response is received
               .On<RandomEncounterContextRequest>(OnRandomEncounterContextRequest)

               // pausing
               .On<ClientTogglePauseOff>(OnClientTogglePauseOff)

               // lobby
               .On<NotifySaveGameSyncStatusChanged>(OnNotifyLobbySyncStatusChanged)
               .On<ClientGameServerConnectionConfirmed>(OnClientGameServerConnectionConfirmed)
               .On<NotifySaveGameChunkReceived>(OnNotifySaveGameChunkReceived)

               // area transitioning
               .On<ClientAreaLoaded>(OnClientAreaLoaded)

               // leveling
               .On<ClientCharacterLevelingRequested>(OnClientCharacterLevelingRequested)

               // combat
               .On<NotifyCombatInitiated>(OnNotifyCombatInitiated)
               .On<NotifyCombatDataCollected>(OnNotifyCombatDataCollected)
               .On<NotifyCombatDataDiscrepancyConfirmed>(OnNotifyCombatDataDiscrepancyConfirmed)
               .On<NotifyCombatUnitsExclusionConfirmed>(OnNotifyCombatUnitsExclusionConfirmed)
               .On<ClientCombatInitializationCompleted>(OnClientCombatInitializationCompleted)
               .On<ClientCombatTurnStarted>(OnClientCombatTurnStarted)
               .On<ClientCombatTurnStartSynchronized>(OnClientCombatTurnSynchronized)
               .On<ClientCombatTurnEndSynchronized>(OnClientCombatTurnEndSynchronized)
               .On<NotifyCombatLocalTurnEnded>(OnNotifyCombatLocalTurnEnded)
               .On<ClientInvalidUnitTurnStartRequested>(OnClientInvalidUnitTurnStartRequested)
               .On<ClientRealTimeStateHash>(OnClientRealTimeStateHash)
               .On<ClientRealTimeCombatCommand>(OnClientRealTimeCombatCommand)
               .On<ClientRealTimePauseRequested>(OnClientRealTimePauseRequested)
               .On<ClientRealTimeModeTransitionAck>(OnClientRealTimeModeTransitionAck)
               .On<ClientRealTimeRepairAck>(OnClientRealTimeRepairAck)
               .On<ClientRealTimeRuleRollFault>(OnClientRealTimeRuleRollFault)
               .On<ClientRealTimeAIActionFault>(OnClientRealTimeAIActionFault)
               .On<ClientRealTimeEntityIdFault>(OnClientRealTimeEntityIdFault)

               // global map & crusade combat
               .On<NotifyTacticalCombatInitializationConfirmed>(OnNotifyTacticalCombatInitializationConfirmed)
               .On<NotifyGlobalMapCrusadeArmyMergeCartClosed>(OnNotifyGlobalMapCrusadeArmyMergeCartClosed)
               .On<NotifyGlobalMapCrusadeArmyInfoShown>(OnNotifyGlobalMapCrusadeArmyInfoShown)
               .On<NotifyGlobalMapCrusadeArmySetLeaderClosed>(OnNotifyGlobalMapCrusadeArmySetLeaderClosed)
               .On<NotifyGlobalMapCrusadeArmyBuyLeaderClosed>(OnNotifyGlobalMapCrusadeArmyBuyLeaderClosed)
               .On<NotifyGlobalMapRecruitmentShown>(OnNotifyGlobalMapRecruitmentShown)
               .On<NotifyGlobalMapRecruitmentClosed>(OnNotifyGlobalMapRecruitmentClosed)
               .On<NotifyGlobalMapCommonPopupShown>(OnNotifyGlobalMapCommonPopupShown)
               .On<ClientArmyCombatTurnSynchronized>(OnClientArmyCombatTurnSynchronized)

               // kingdom
               .On<NotifyKingdomNavigationChanged>(OnNotifyKingdomNavigationChanged)

               // dialogs
               .On<ClientDialogCueAnswerSuggested>(OnClientDialogCueAnswerSuggested)
               .On<ClientDialogStartRequested>(OnClientDialogStartRequested)
               .On<ClientDialogCueWitnessed>(OnClientDialogCueWitnessed)

               // pause
               .On<ClientGameAutoPaused>(OnClientGameAutoPaused)

               // inventory
               .On<NotifyPolymorphicItemCreationRequested>(OnNotifyPolymorphicItemCreationRequested)
               ;
        }

        private void OnClientArmyCombatTurnSynchronized(long receivedFrom, ClientArmyCombatTurnSynchronized message)
        {
            lock (ActionLock)
            {
                if (Game.ArmyCombat?.Turn != null)
                {
                    Game.ArmyCombat.Turn.SyncedPlayers.Add(receivedFrom);
                }
            }
        }

        private async void OnClientInvalidUnitTurnStartRequested(long receivedFrom, ClientInvalidUnitTurnStartRequested message)
        {
            var player = GetPlayer(receivedFrom);
            if (player == null)
            {
                return;
            }

            if (!string.Equals(Game.Combat.Turn?.UnitId, message.UnitId, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning("Skipping invalid unit notification as unit doesn't match. TurnUnitId={TurnUnitId}, UnitId={UnitId}", Game.Combat.Turn?.UnitId, message.UnitId);
                return;
            }

            lock (ActionLock)
            {
                Game.Combat.Turn = null;
                Game.Combat.PlayersNextTurnInitialization.Clear();
            }

            // killing unit seems fine? desynced (did not spawn for everyone) unit cannot start turn anyway, so it must have been killed during combat startup where unit auto-kill is not enabled yet
            await CombatInteraction.KillUnitAndResetTurnAsync(player, message.UnitId);
        }

        private async void OnNotifyCombatInitiated(long receivedFrom, NotifyCombatInitiated message)
        {
            SetPauseCooldown();

            var isInCombat = await WaitWhileTrue(() => Game.Combat == null, "Waiting for combat to be initiated", TimeSpan.FromSeconds(30)); // cutscenes etc
            if (!isInCombat)
            {
                Logger.LogError("Combat has not been started on host");
                return;
            }

            // client combat has been restarted for some reason (game does it sometimes seemingly for no reason)
            // all we can do is to restart it locally as well
            if (Game.Combat.Stage != NetworkCombatStage.Initiating)
            {
                Logger.LogWarning("Client combat has been restarted for some reason, but host already advanced further in startup sequence. PlayerId={PlayerId}", receivedFrom);
                var player = GetPlayer(receivedFrom);
                PlayerNotification.AddCombatText(WellKnownKeys.GameNotifications.Combat.Start.DesyncedStartup.Client.Key, CombatTextSeverity.Critical, player?.Name);

                await WaitWhileTrue(() => CombatInteraction.IsRiderActive(), "Waiting for current turn to end before resetting combat");

                // this will also trigger restart for all clients in the lobby
                CleanupUntargetableUnitsState();
                Game.Combat = null;
                await CombatInteraction.ForceResetCombatAsync();

                await WaitWhileTrue(() => Game.Combat == null || !Game.Combat.IsInitiated, "Waiting for combat to restart");
                Game.Combat.PlayersInCombat.TryAdd(message.PlayerId, true);
                Logger.LogInformation("Corrupted player has been re-added after combat restart. PlayerId={PlayerId}", message.PlayerId);
                return;
            }

            Game.Combat.PlayersInCombat.TryAdd(message.PlayerId, true);
            Logger.LogInformation("IsInCombat state has been registered. PlayerId={PlayerId}", message.PlayerId);
        }

        private void OnNotifyCombatDataCollected(long receivedFrom, NotifyCombatDataCollected message)
        {
            Game.Combat.InitialUnitsInCombat.TryAdd(message.PlayerId, [.. message.Units]);
        }

        private void OnNotifyCombatUnitsExclusionConfirmed(long receivedFrom, NotifyCombatUnitsExclusionConfirmed message)
        {
            Game.Combat.UnavailableUnits.TryRemove(receivedFrom, out _);
        }

        private void OnNotifyCombatDataDiscrepancyConfirmed(long receivedFrom, NotifyCombatDataDiscrepancyConfirmed message)
        {
            Game.Combat.UnavailableUnits.TryAdd(receivedFrom, message.UnavailableUnits);
        }

        private void OnNotifySaveGameChunkReceived(long receivedFrom, NotifySaveGameChunkReceived message)
        {
            var transfer = GetSaveGameTransferData(receivedFrom);
            transfer.ConfirmedChunk = message.ChunkNumber;

            var currentProgress = Game.StartUp.SaveGameTransfer.Data.ToDictionary(x => x.Key, x => x.Value.ConfirmedChunk / (float)Game.StartUp.SaveGameTransfer.TotalChunks);
            OnSaveGameTransferProgressChanged?.Invoke(currentProgress);

            // waiting for the latest chunk confirmation to arrive before sending new data
            if (transfer.SentChunk != message.ChunkNumber)
            {
                return;
            }

            var progressMessage = new NotifySaveGameTransferProgressChanged
            {
                Progress = currentProgress
            };
            Send(progressMessage);

            if (transfer.MaxOffset != Game.StartUp.SaveGameTransfer.Content.Length)
            {
                var settings = SettingsService.GetSettings();
                transfer.MaxOffset = Math.Min(Game.StartUp.SaveGameTransfer.Content.Length, transfer.CurrentOffset + settings.SaveGameChunkSize * Game.StartUp.SaveGameTransfer.BatchSize);
                SendSaveChunks(receivedFrom, Game.StartUp.SaveGameTransfer.Content, transfer, settings.SaveGameChunkSize);
            }
        }

        private void OnNotifyKingdomNavigationChanged(long receivedFrom, NotifyKingdomNavigationChanged message)
        {
            var navigation = Mapper.Map<KingdomNavigationType>(message.Type);
            Game.PlayersInKingdomNavigationType.AddOrUpdate(receivedFrom, navigation, (key, existing) => navigation);
        }

        private void OnClientRealTimeStateHash(long receivedFrom, ClientRealTimeStateHash message)
        {
            _mainThreadAccessor.Post(() => ProcessClientRealTimeStateHash(receivedFrom, message));
        }

        private void OnClientRealTimeRuleRollFault(long receivedFrom, ClientRealTimeRuleRollFault message)
        {
            _mainThreadAccessor.Post(() =>
            {
                if (message == null || GetPlayer(receivedFrom) == null || !IsRealTimeCombatActive
                    || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable
                    || message.Epoch != Game.RealTimeCombat.Epoch || message.Sequence <= 0)
                    return;
                Logger.LogWarning("Client could not obtain an authoritative combat roll; starting repair. PlayerId={PlayerId}, Epoch={Epoch}, Sequence={Sequence}, Key={Key}, Reason={Reason}",
                    receivedFrom, message.Epoch, message.Sequence, message.Key, message.Reason);
                _realTimeCriticalFaulted = true;
                _ = BeginRealTimeRepairAsync();
            });
        }

        private void OnClientRealTimeAIActionFault(long receivedFrom, ClientRealTimeAIActionFault message)
        {
            _mainThreadAccessor.Post(() =>
            {
                if (message == null || GetPlayer(receivedFrom) == null || !IsRealTimeCombatActive
                    || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable
                    || message.Epoch != Game.RealTimeCombat.Epoch || string.IsNullOrWhiteSpace(message.UnitId))
                    return;
                Logger.LogWarning("Client could not apply authoritative combat AI action; starting protected fallback. PlayerId={PlayerId}, Epoch={Epoch}, UnitId={UnitId}, ActionId={ActionId}, Reason={Reason}",
                    receivedFrom, message.Epoch, message.UnitId, message.ActionId, message.Reason);
                _realTimeCriticalFaulted = true;
                _ = BeginRealTimeRepairAsync();
            });
        }

        private void OnClientRealTimeEntityIdFault(long receivedFrom, ClientRealTimeEntityIdFault message)
        {
            _mainThreadAccessor.Post(() =>
            {
                if (message == null || GetPlayer(receivedFrom) == null || !IsRealTimeCombatActive
                    || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable
                    || message.Epoch != Game.RealTimeCombat.Epoch || message.Sequence <= 0)
                    return;
                Logger.LogWarning("Client could not obtain host entity ID; starting protected fallback. PlayerId={PlayerId}, Epoch={Epoch}, Sequence={Sequence}, Key={Key}, Reason={Reason}",
                    receivedFrom, message.Epoch, message.Sequence, message.Key, message.Reason);
                _realTimeCriticalFaulted = true;
                _ = BeginRealTimeRepairAsync();
            });
        }

        private void ProcessClientRealTimeStateHash(long receivedFrom, ClientRealTimeStateHash message)
        {
            if (Game?.RealTimeCombat == null || message == null || GetPlayer(receivedFrom) == null)
            {
                return;
            }

            var coordinator = new RealTimeCombatCoordinator(Game.RealTimeCombat);
            if (!_realTimeSnapshotHashes.TryGetValue((message.Epoch, message.Sequence), out var expectedHash)
                || !coordinator.TryCompareClientSnapshot(message.Epoch, message.Sequence, expectedHash, message.CanonicalStateHash, out var requiresRepair))
            {
                Logger.LogDebug("Ignoring stale real-time state hash. PlayerId={PlayerId}, Epoch={Epoch}, Sequence={Sequence}", receivedFrom, message.Epoch, message.Sequence);
                return;
            }

            if (_realTimeSnapshotSentAt.TryRemove((message.Epoch, message.Sequence), out var sentAt))
            {
                _consecutiveMissingRealTimeReplies = 0;
                var elapsed = Math.Max(0, Math.Min(60_000, (int)(DateTime.UtcNow - sentAt).TotalMilliseconds));
                Game.RealTimeCombat.ResponseTimeMs = Game.RealTimeCombat.ResponseTimeMs == 0
                    ? elapsed
                    : (Game.RealTimeCombat.ResponseTimeMs * 4 + elapsed) / 5;
            }

            if (requiresRepair || !message.Applied)
            {
                Logger.LogWarning("Real-time moving snapshot differs or failed to apply; starting paused verification. PlayerId={PlayerId}, Epoch={Epoch}, Sequence={Sequence}, HostHash={HostHash}, ClientHash={ClientHash}, Applied={Applied}, Reason={Reason}", receivedFrom, message.Epoch, message.Sequence, expectedHash, message.CanonicalStateHash, message.Applied, message.FailureReason);
                _ = BeginRealTimeRepairAsync();
            }
        }

        public async Task BeginRealTimeRepairAsync()
        {
            if (!IsRealTimeCombatActive || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable)
            {
                return;
            }
            var coordinator = new RealTimeCombatCoordinator(Game.RealTimeCombat);
            coordinator.BeginRepair();
            _consecutiveMissingRealTimeReplies = 0;
            Game.RealTimeCombat.RequiredPlayers = GetSyncedPlayers().Select(x => x.Id).Append(Game.LocalPlayerId).ToHashSet();
            Game.RealTimeCombat.ReadyPlayers = [Game.LocalPlayerId];
            _realTimeRepairStage = RealTimeRepairStage.Prepare;
            _realTimeRepairDeadline = DateTime.UtcNow.AddSeconds(30);
            _realTimeRepairRetransmissions = 0;
            try
            {
                await GameInteraction.SetPauseAsync(true);
                _lastRealTimeRepairMessage = new NotifyRealTimeRepair
                {
                    Epoch = Game.RealTimeCombat.Epoch,
                    Stage = (int)RealTimeRepairStage.Prepare,
                    Mode = (int)CombatMode.RealTimeWithPause
                };
                Send(_lastRealTimeRepairMessage);
                if (Game.RealTimeCombat.RequiredPlayers.IsSubsetOf(Game.RealTimeCombat.ReadyPlayers))
                {
                    await StartRealTimeRepairVerificationAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to pause for real-time combat verification. Epoch={Epoch}", Game.RealTimeCombat.Epoch);
                GameInteraction.SetPause(true);
            }
        }

        private async Task StartRealTimeRepairVerificationAsync()
        {
            if (Game?.RealTimeCombat?.Phase != CombatSynchronizationPhase.Repairing
                || _realTimeRepairStage != RealTimeRepairStage.Prepare
                || Interlocked.CompareExchange(ref _realTimeRepairVerificationInProgress, 1, 0) != 0)
            {
                return;
            }
            try
            {
                if (_realTimeCriticalFaulted)
                {
                    Logger.LogWarning("A combat rule or AI choice could not be confirmed; switching directly to the protected turn-based fallback. Epoch={Epoch}",
                        Game.RealTimeCombat.Epoch);
                    new RealTimeCombatCoordinator(Game.RealTimeCombat).ForceFallbackAfterUntrustedRuleResult();
                    _ = StartRealTimeRepairFallbackAsync();
                    return;
                }
                var epoch = Game.RealTimeCombat.Epoch;
                var checkpoint = await CombatInteraction.GetRealTimeCombatStateAsync();
                if (Game?.RealTimeCombat?.Epoch != epoch
                    || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Repairing
                    || _realTimeRepairStage != RealTimeRepairStage.Prepare)
                    return;
                _realTimeRepairCheckpoint = checkpoint;
                Game.RealTimeCombat.CanonicalStateHash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = _realTimeRepairCheckpoint });
                _realTimeRepairHasMismatch = false;
                _realTimeRepairStage = RealTimeRepairStage.Verify;
                Game.RealTimeCombat.ReadyPlayers = [Game.LocalPlayerId];
                _realTimeRepairDeadline = DateTime.UtcNow.AddSeconds(30);
                _realTimeRepairRetransmissions = 0;
                _lastRealTimeRepairMessage = new NotifyRealTimeRepair
                {
                    Epoch = Game.RealTimeCombat.Epoch,
                    Stage = (int)RealTimeRepairStage.Verify,
                    Mode = (int)CombatMode.RealTimeWithPause,
                    CanonicalStateHash = Game.RealTimeCombat.CanonicalStateHash,
                    State = Mapper.Map<Networking.Messages.Contracts.NetworkCombatState>(_realTimeRepairCheckpoint)
                };
                Send(_lastRealTimeRepairMessage);
                if (Game.RealTimeCombat.RequiredPlayers.IsSubsetOf(Game.RealTimeCombat.ReadyPlayers))
                {
                    CompleteRealTimeRepair();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to sample host state for paused real-time verification. Epoch={Epoch}",
                    Game?.RealTimeCombat?.Epoch);
                GameInteraction.SetPause(true);
            }
            finally
            {
                Interlocked.Exchange(ref _realTimeRepairVerificationInProgress, 0);
            }
        }

        private void StartRealTimeRepairApply()
        {
            _realTimeRepairStage = RealTimeRepairStage.Apply;
            Game.RealTimeCombat.ReadyPlayers = [Game.LocalPlayerId];
            _realTimeRepairDeadline = DateTime.UtcNow.AddSeconds(30);
            _realTimeRepairRetransmissions = 0;
            _lastRealTimeRepairMessage = new NotifyRealTimeRepair
            {
                Epoch = Game.RealTimeCombat.Epoch,
                Stage = (int)RealTimeRepairStage.Apply,
                Mode = (int)CombatMode.RealTimeWithPause,
                CanonicalStateHash = Game.RealTimeCombat.CanonicalStateHash,
                State = Mapper.Map<Networking.Messages.Contracts.NetworkCombatState>(_realTimeRepairCheckpoint)
            };
            Send(_lastRealTimeRepairMessage);
            if (Game.RealTimeCombat.RequiredPlayers.IsSubsetOf(Game.RealTimeCombat.ReadyPlayers))
            {
                CompleteRealTimeRepair();
            }
        }

        private void CompleteRealTimeRepair()
        {
            var hash = Game.RealTimeCombat.CanonicalStateHash;
            new RealTimeCombatCoordinator(Game.RealTimeCombat).CompleteRepair(hash, hash);
            FinishRealTimeRepair(CombatMode.RealTimeWithPause);
        }

        private void FinishRealTimeRepair(CombatMode mode)
        {
            _realTimeRepairCheckpoint = null;
            Game.RealTimeCombat.Phase = mode == CombatMode.TurnBased
                ? CombatSynchronizationPhase.FallingBackToTurnBased
                : CombatSynchronizationPhase.Repairing;
            Game.RealTimeCombat.RequiredPlayers = GetSyncedPlayers().Select(x => x.Id)
                .Append(Game.LocalPlayerId).ToHashSet();
            Game.RealTimeCombat.ReadyPlayers = [Game.LocalPlayerId];
            _realTimeRepairStage = RealTimeRepairStage.Resume;
            _realTimeRepairDeadline = DateTime.UtcNow.AddSeconds(10);
            _lastRealTimeRepairMessage = new NotifyRealTimeRepair
            {
                Epoch = Game.RealTimeCombat.Epoch,
                Stage = (int)RealTimeRepairStage.Resume,
                Mode = (int)mode,
                CanonicalStateHash = Game.RealTimeCombat.CanonicalStateHash
            };
            Send(_lastRealTimeRepairMessage);
            TryFinalizeRealTimeRepairResume();
        }

        private void TryFinalizeRealTimeRepairResume()
        {
            if (_realTimeRepairStage != RealTimeRepairStage.Resume || Game?.RealTimeCombat == null)
            {
                return;
            }
            var connectedPlayers = GetSyncedPlayers().Select(x => x.Id)
                .Append(Game.LocalPlayerId).ToHashSet();
            Game.RealTimeCombat.RequiredPlayers.IntersectWith(connectedPlayers);
            if (!Game.RealTimeCombat.RequiredPlayers.IsSubsetOf(Game.RealTimeCombat.ReadyPlayers))
            {
                return;
            }
            Game.RealTimeCombat.Phase = CombatSynchronizationPhase.Stable;
            Game.RealTimeCombat.RequiredPlayers.Clear();
            Game.RealTimeCombat.ReadyPlayers.Clear();
            _realTimeRepairStage = null;
            _lastRealTimeRepairMessage = null;
            _realTimeRepairDeadline = default;
            GameInteraction.SetPause(Game.RealTimeCombat.IsManualPauseActive || Game.ForcedPause != null);
        }

        private async Task FailRealTimeRepairAttemptAsync(ulong clientHash)
        {
            if (Game?.RealTimeCombat?.Phase != CombatSynchronizationPhase.Repairing)
            {
                return;
            }
            var hostHash = Game.RealTimeCombat.CanonicalStateHash;
            var differingHash = clientHash == hostHash ? hostHash ^ 1UL : clientHash;
            new RealTimeCombatCoordinator(Game.RealTimeCombat).CompleteRepair(hostHash, differingHash);
            if (Game.RealTimeCombat.Phase == CombatSynchronizationPhase.FallingBackToTurnBased)
            {
                await StartRealTimeRepairFallbackAsync();
                return;
            }
            new RealTimeCombatCoordinator(Game.RealTimeCombat).BeginRepair();
            _consecutiveMissingRealTimeReplies = 0;
            Game.RealTimeCombat.ReadyPlayers = [Game.LocalPlayerId];
            _realTimeRepairStage = RealTimeRepairStage.Prepare;
            _realTimeRepairDeadline = DateTime.UtcNow.AddSeconds(30);
            _realTimeRepairRetransmissions = 0;
            _lastRealTimeRepairMessage = new NotifyRealTimeRepair
            {
                Epoch = Game.RealTimeCombat.Epoch,
                Stage = (int)RealTimeRepairStage.Prepare,
                Mode = (int)CombatMode.RealTimeWithPause
            };
            Send(_lastRealTimeRepairMessage);
            if (Game.RealTimeCombat.RequiredPlayers.IsSubsetOf(Game.RealTimeCombat.ReadyPlayers))
            {
                await StartRealTimeRepairVerificationAsync();
            }
        }

        private async Task StartRealTimeRepairFallbackAsync()
        {
            try
            {
                await GameInteraction.SetTurnBasedCombatModeAsync(true);
                _realTimeRepairCheckpoint = await CombatInteraction.GetRealTimeCombatStateAsync();
                Game.RealTimeCombat.CanonicalStateHash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = _realTimeRepairCheckpoint });
                Game.RealTimeCombat.ReadyPlayers = [Game.LocalPlayerId];
                _realTimeRepairStage = RealTimeRepairStage.Fallback;
                _realTimeRepairDeadline = DateTime.UtcNow.AddSeconds(30);
                _realTimeRepairRetransmissions = 0;
                _lastRealTimeRepairMessage = new NotifyRealTimeRepair
                {
                    Epoch = Game.RealTimeCombat.Epoch,
                    Stage = (int)RealTimeRepairStage.Fallback,
                    Mode = (int)CombatMode.TurnBased,
                    CanonicalStateHash = Game.RealTimeCombat.CanonicalStateHash,
                    State = Mapper.Map<Networking.Messages.Contracts.NetworkCombatState>(_realTimeRepairCheckpoint)
                };
                Send(_lastRealTimeRepairMessage);
                if (Game.RealTimeCombat.RequiredPlayers.IsSubsetOf(Game.RealTimeCombat.ReadyPlayers))
                {
                    CommitRealTimeRepairFallback();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to apply automatic turn-based fallback. Epoch={Epoch}; game remains paused and saving is blocked", Game.RealTimeCombat.Epoch);
                GameInteraction.SetPause(true);
            }
        }

        private void CommitRealTimeRepairFallback()
        {
            new RealTimeCombatCoordinator(Game.RealTimeCombat).CommitFallback(Game.RealTimeCombat.CanonicalStateHash);
            _realTimeCriticalFaulted = false;
            FinishRealTimeRepair(CombatMode.TurnBased);
        }

        private void CheckRealTimeRepairTimeout()
        {
            if (!_realTimeRepairStage.HasValue || _realTimeRepairDeadline == default
                || DateTime.UtcNow <= _realTimeRepairDeadline)
            {
                return;
            }
            _realTimeRepairDeadline = DateTime.UtcNow.AddSeconds(30);
            if (_realTimeRepairStage == RealTimeRepairStage.Resume)
            {
                TryFinalizeRealTimeRepairResume();
                if (_realTimeRepairStage == RealTimeRepairStage.Resume)
                {
                    _realTimeRepairDeadline = DateTime.UtcNow.AddSeconds(10);
                    Logger.LogWarning("Waiting for peer to acknowledge repair completion. Epoch={Epoch}", Game.RealTimeCombat.Epoch);
                    Send(_lastRealTimeRepairMessage);
                }
                return;
            }
            if (_realTimeRepairStage == RealTimeRepairStage.Fallback)
            {
                Logger.LogWarning("Waiting for peer to acknowledge turn-based fallback. Epoch={Epoch}", Game.RealTimeCombat.Epoch);
                if (_lastRealTimeRepairMessage != null) Send(_lastRealTimeRepairMessage);
                return;
            }
            if (_lastRealTimeRepairMessage != null && _realTimeRepairRetransmissions++ < 2)
            {
                _realTimeRepairDeadline = DateTime.UtcNow.AddSeconds(10);
                Logger.LogWarning("Retransmitting real-time repair barrier. Epoch={Epoch}, Stage={Stage}, Attempt={Attempt}", Game.RealTimeCombat.Epoch, _realTimeRepairStage, _realTimeRepairRetransmissions);
                Send(_lastRealTimeRepairMessage);
                return;
            }
            Logger.LogWarning("Real-time repair barrier timed out. Epoch={Epoch}, Stage={Stage}", Game.RealTimeCombat.Epoch, _realTimeRepairStage);
            _ = FailRealTimeRepairAttemptAsync(0);
        }

        private void OnClientRealTimeRepairAck(long receivedFrom, ClientRealTimeRepairAck message)
        {
            _mainThreadAccessor.Post(async () =>
            {
                try
                {
                    if (Game?.RealTimeCombat == null || message == null || !_realTimeRepairStage.HasValue
                        || message.Epoch != Game.RealTimeCombat.Epoch || message.PlayerId != receivedFrom
                        || (int)_realTimeRepairStage.Value != message.Stage
                        || !Game.RealTimeCombat.RequiredPlayers.Contains(receivedFrom))
                    {
                        return;
                    }
                    if (!message.Applied)
                    {
                        if (_realTimeRepairStage == RealTimeRepairStage.Fallback)
                        {
                            Logger.LogError("Peer could not apply the turn-based fallback checkpoint; combat stays paused and saving stays blocked. PlayerId={PlayerId}, Epoch={Epoch}, HostHash={HostHash}, ClientHash={ClientHash}", receivedFrom, message.Epoch, Game.RealTimeCombat.CanonicalStateHash, message.CanonicalStateHash);
                            GameInteraction.SetPause(true);
                            return;
                        }
                        await FailRealTimeRepairAttemptAsync(message.CanonicalStateHash);
                        return;
                    }
                    if (_realTimeRepairStage == RealTimeRepairStage.Prepare)
                    {
                        Game.RealTimeCombat.ReadyPlayers.Add(receivedFrom);
                        if (Game.RealTimeCombat.RequiredPlayers.IsSubsetOf(Game.RealTimeCombat.ReadyPlayers))
                        {
                            await StartRealTimeRepairVerificationAsync();
                        }
                    }
                    else if (_realTimeRepairStage == RealTimeRepairStage.Verify)
                    {
                        _realTimeRepairHasMismatch |= message.CanonicalStateHash != Game.RealTimeCombat.CanonicalStateHash
                            || message.TemporalMismatch;
                        if (message.TemporalMismatch)
                            Logger.LogWarning("Peer reports temporal combat divergence. PlayerId={PlayerId}, Epoch={Epoch}",
                                receivedFrom, message.Epoch);
                        Game.RealTimeCombat.ReadyPlayers.Add(receivedFrom);
                        if (Game.RealTimeCombat.RequiredPlayers.IsSubsetOf(Game.RealTimeCombat.ReadyPlayers))
                        {
                            if (_realTimeRepairHasMismatch) StartRealTimeRepairApply();
                            else CompleteRealTimeRepair();
                        }
                    }
                    else if (_realTimeRepairStage == RealTimeRepairStage.Apply)
                    {
                        if (message.CanonicalStateHash != Game.RealTimeCombat.CanonicalStateHash)
                        {
                            await FailRealTimeRepairAttemptAsync(message.CanonicalStateHash);
                            return;
                        }
                        Game.RealTimeCombat.ReadyPlayers.Add(receivedFrom);
                        if (Game.RealTimeCombat.RequiredPlayers.IsSubsetOf(Game.RealTimeCombat.ReadyPlayers))
                        {
                            CompleteRealTimeRepair();
                        }
                    }
                    else if (_realTimeRepairStage == RealTimeRepairStage.Fallback)
                    {
                        if (message.CanonicalStateHash != Game.RealTimeCombat.CanonicalStateHash)
                        {
                            Logger.LogError("Peer could not verify turn-based fallback. PlayerId={PlayerId}, Epoch={Epoch}", receivedFrom, message.Epoch);
                            return;
                        }
                        Game.RealTimeCombat.ReadyPlayers.Add(receivedFrom);
                        if (Game.RealTimeCombat.RequiredPlayers.IsSubsetOf(Game.RealTimeCombat.ReadyPlayers))
                        {
                            CommitRealTimeRepairFallback();
                        }
                    }
                    else if (_realTimeRepairStage == RealTimeRepairStage.Resume
                        && message.CanonicalStateHash == Game.RealTimeCombat.CanonicalStateHash)
                    {
                        Game.RealTimeCombat.ReadyPlayers.Add(receivedFrom);
                        TryFinalizeRealTimeRepairResume();
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Unable to process real-time repair acknowledgement. PlayerId={PlayerId}, Epoch={Epoch}", receivedFrom, message?.Epoch);
                }
            });
        }

        private void OnClientRealTimeCombatCommand(long receivedFrom, ClientRealTimeCombatCommand message)
        {
            // Authoritative ordering follows execution on Unity's main thread, not packet arrival.
            _mainThreadAccessor.Post(() => AcceptRealTimeCombatCommand(receivedFrom, message, executeOnHost: true));
        }

        public bool PublishLocalRealTimeCombatCommand(RealTimeCommandKind kind, NetworkUnitMoveTo movement = null, NetworkUnitAttack attack = null, NetworkAbilityUse ability = null)
        {
            if (!IsRealTimeCombatActive || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable)
            {
                return false;
            }

            var message = new ClientRealTimeCombatCommand
            {
                Epoch = Game.RealTimeCombat.Epoch,
                PlayerId = Game.LocalPlayerId,
                Kind = (int)kind,
                Movement = movement == null ? null : Mapper.Map<Networking.Messages.Contracts.NetworkUnitMoveTo>(movement),
                Attack = attack == null ? null : Mapper.Map<Networking.Messages.Contracts.NetworkUnitAttack>(attack),
                Ability = ability == null ? null : Mapper.Map<Networking.Messages.Contracts.NetworkAbilityUse>(ability)
            };
            lock (_realTimeCommandLock)
            {
                if (_realTimeLocalCommandEpoch != message.Epoch)
                {
                    _realTimeLocalCommandEpoch = message.Epoch;
                    _nextRealTimeLocalCommandSequence = 0;
                }
                message.Sequence = ++_nextRealTimeLocalCommandSequence;
            }
            return AcceptRealTimeCombatCommand(Game.LocalPlayerId, message, executeOnHost: false);
        }

        private bool AcceptRealTimeCombatCommand(long receivedFrom, ClientRealTimeCombatCommand message, bool executeOnHost)
        {
            if (Game?.Combat?.Stage != NetworkCombatStage.Running || Game.RealTimeCombat == null
                || GetPlayer(receivedFrom) == null || !RealTimeCombatCommandValidator.TryGetUnitId(message, out var unitId))
            {
                Logger.LogWarning("Rejecting malformed or out-of-combat real-time command. PlayerId={PlayerId}", receivedFrom);
                return false;
            }

            var owners = Game.Characters
                .Where(x => x?.Owner != null && !string.IsNullOrWhiteSpace(x.UnitId))
                .GroupBy(x => x.UnitId, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First().Owner.Id, StringComparer.Ordinal);
            var envelope = new RealTimeCommandEnvelope
            {
                Epoch = message.Epoch,
                Sequence = message.Sequence,
                PlayerId = message.PlayerId,
                UnitId = unitId,
                Kind = (RealTimeCommandKind)message.Kind
            };
            long authoritativeSequence;
            lock (_realTimeCommandLock)
            {
                if (!ReferenceEquals(_realTimeCommandGateState, Game.RealTimeCombat))
                {
                    _realTimeCommandGateState = Game.RealTimeCombat;
                    _realTimeCommandGate = new RealTimeCommandGate(_realTimeCommandGateState);
                }

                var rejection = _realTimeCommandGate.Validate(envelope, receivedFrom, owners);
                if (rejection != RealTimeCommandRejection.None)
                {
                    Logger.LogWarning("Rejecting real-time command. PlayerId={PlayerId}, UnitId={UnitId}, Epoch={Epoch}, Sequence={Sequence}, Reason={Reason}", receivedFrom, unitId, message.Epoch, message.Sequence, rejection);
                    return false;
                }

                if (executeOnHost)
                {
                    try
                    {
                        if (!CombatInteraction.ExecuteRealTimeCombatCommand(
                            (RealTimeCommandKind)message.Kind,
                            message.Movement == null ? null : Mapper.Map<NetworkUnitMoveTo>(message.Movement),
                            message.Attack == null ? null : Mapper.Map<NetworkUnitAttack>(message.Attack),
                            message.Ability == null ? null : Mapper.Map<NetworkAbilityUse>(message.Ability)))
                        {
                            Logger.LogWarning("Real-time command was rejected by game execution. PlayerId={PlayerId}, UnitId={UnitId}, Epoch={Epoch}, Sequence={Sequence}", receivedFrom, unitId, message.Epoch, message.Sequence);
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Real-time command failed on host. PlayerId={PlayerId}, UnitId={UnitId}, Epoch={Epoch}, Sequence={Sequence}", receivedFrom, unitId, message.Epoch, message.Sequence);
                        return false;
                    }
                }

                // A command rejected by the game must not consume its sender's sequence.
                if (!_realTimeCommandGate.TryCommit(envelope))
                {
                    Logger.LogWarning("Real-time command changed epoch or order during game execution; scheduling verification. PlayerId={PlayerId}, Epoch={Epoch}, Sequence={Sequence}", receivedFrom, message.Epoch, message.Sequence);
                    _mainThreadAccessor.Post(() => _ = BeginRealTimeRepairAsync());
                    return false;
                }
                authoritativeSequence = new RealTimeCombatCoordinator(Game.RealTimeCombat).NextAuthoritativeSequence();
            }

            var accepted = new NotifyRealTimeCombatCommandAccepted
            {
                Epoch = message.Epoch,
                AuthoritativeSequence = authoritativeSequence,
                PlayerId = receivedFrom,
                ClientSequence = message.Sequence,
                Kind = message.Kind,
                Movement = message.Movement,
                Attack = message.Attack,
                Ability = message.Ability
            };
            RealTimeAcceptedOrderLedger.Record(Game.RealTimeCombat, accepted);
            Send(accepted);
            return true;
        }

        public bool RequestLocalRealTimePause(bool wantsPause)
        {
            if (!IsRealTimeCombatActive || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable)
            {
                return false;
            }

            long requestSequence;
            lock (_realTimePauseLock)
            {
                if (_realTimePauseRequestEpoch != Game.RealTimeCombat.Epoch)
                {
                    _realTimePauseRequestEpoch = Game.RealTimeCombat.Epoch;
                    _nextLocalRealTimePauseRequestSequence = 0;
                }
                requestSequence = ++_nextLocalRealTimePauseRequestSequence;
            }

            return ApplyRealTimePauseRequest(Game.LocalPlayerId, new ClientRealTimePauseRequested
            {
                Epoch = Game.RealTimeCombat.Epoch,
                RequestSequence = requestSequence,
                PlayerId = Game.LocalPlayerId,
                WantsPause = wantsPause
            });
        }

        public bool SetRealTimePausePolicy(PausePolicy policy)
        {
            if (!IsActive || Game?.RealTimeCombat == null
                || !Enum.IsDefined(typeof(PausePolicy), policy)
                || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable)
            {
                return false;
            }

            lock (_realTimePauseLock)
            {
                Game.RealTimeCombat.PausePolicy = policy;
                // A policy change invalidates any outstanding consensus votes.
                Game.RealTimeCombat.ResumeVotes.Clear();
                Game.RealTimeCombat.PauseSequence++;
                Send(new NotifyRealTimePauseChanged
                {
                    Epoch = Game.RealTimeCombat.Epoch,
                    Sequence = Game.RealTimeCombat.PauseSequence,
                    IsPaused = Game.RealTimeCombat.IsManualPauseActive,
                    Policy = (int)policy,
                    PausedPlayers = [.. Game.RealTimeCombat.PausedPlayers],
                    ResumeVotes = []
                });
                SendRealTimePreference();
            }

            Logger.LogInformation("Real-time pause policy changed. Policy={Policy}, Epoch={Epoch}", policy, Game.RealTimeCombat.Epoch);
            return true;
        }

        public bool SetPreferredRealTimeCombatMode(CombatMode mode)
        {
            if (!IsActive || Game?.RealTimeCombat == null
                || !Enum.IsDefined(typeof(CombatMode), mode)
                || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable)
            {
                return false;
            }

            Game.RealTimeCombat.PreferredMode = mode;
            SendRealTimePreference();
            Logger.LogInformation("Preferred combat mode changed. Mode={Mode}", mode);
            return true;
        }

        private void SendRealTimePreference(long? playerId = null)
        {
            var state = Game.RealTimeCombat;
            var message = new NotifyRealTimeCombatPreference
            {
                Sequence = ++state.PreferenceSequence,
                PreferredMode = (int)state.PreferredMode,
                PausePolicy = (int)state.PausePolicy
            };
            if (playerId.HasValue) Send(playerId.Value, message);
            else Send(message);
        }

        private void OnClientRealTimePauseRequested(long receivedFrom, ClientRealTimePauseRequested message)
        {
            ApplyRealTimePauseRequest(receivedFrom, message);
        }

        private bool ApplyRealTimePauseRequest(long receivedFrom, ClientRealTimePauseRequested message)
        {
            if (Game?.Combat?.Stage != NetworkCombatStage.Running || Game.RealTimeCombat == null
                || message == null || message.PlayerId != receivedFrom || message.Epoch != Game.RealTimeCombat.Epoch
                || message.RequestSequence <= 0 || GetPlayer(receivedFrom) == null)
            {
                return false;
            }

            NotifyRealTimePauseChanged notification;
            lock (_realTimePauseLock)
            {
                if (_realTimePauseRequestEpoch != message.Epoch)
                {
                    _lastRealTimePauseRequestByPlayer.Clear();
                    _realTimePauseRequestEpoch = message.Epoch;
                }
                if (_lastRealTimePauseRequestByPlayer.TryGetValue(receivedFrom, out var last)
                    && message.RequestSequence <= last)
                {
                    return false;
                }

                var players = GetSyncedPlayers().Select(x => x.Id).Append(Game.LocalPlayerId).Distinct().ToList();
                if (!new RealTimePauseCoordinator(Game.RealTimeCombat).TryRequest(receivedFrom, Game.LocalPlayerId, message.WantsPause, players))
                {
                    Logger.LogDebug("Real-time pause request was denied. PlayerId={PlayerId}, WantsPause={WantsPause}, Policy={Policy}", receivedFrom, message.WantsPause, Game.RealTimeCombat.PausePolicy);
                    return false;
                }

                _lastRealTimePauseRequestByPlayer[receivedFrom] = message.RequestSequence;
                notification = new NotifyRealTimePauseChanged
                {
                    Epoch = Game.RealTimeCombat.Epoch,
                    Sequence = Game.RealTimeCombat.PauseSequence,
                    IsPaused = Game.RealTimeCombat.IsManualPauseActive,
                    Policy = (int)Game.RealTimeCombat.PausePolicy,
                    PausedPlayers = [.. Game.RealTimeCombat.PausedPlayers],
                    ResumeVotes = [.. Game.RealTimeCombat.ResumeVotes]
                };
            }

            GameInteraction.SetPause(notification.IsPaused || Game.ForcedPause != null);
            Send(notification);
            return true;
        }

        private async void OnNotifyCombatLocalTurnEnded(long receivedFrom, NotifyCombatLocalTurnEnded message)
        {
            if (!CombatTurnMessageGate.IsCurrentEndMessage(Game?.Combat?.Turn?.UnitId, message.UnitId, receivedFrom, message.PlayerId, validateSender: true))
            {
                Logger.LogWarning("Ignoring stale or invalid turn-end request. ReceivedFrom={ReceivedFrom}, DeclaredPlayerId={PlayerId}, MessageUnitId={MessageUnitId}, CurrentUnitId={CurrentUnitId}", receivedFrom, message.PlayerId, message.UnitId, Game?.Combat?.Turn?.UnitId);
                return;
            }

            await WaitForTurnToBeCompletedAsync();

            if (!CombatTurnMessageGate.IsCurrentEndMessage(Game?.Combat?.Turn?.UnitId, message.UnitId, receivedFrom, message.PlayerId, validateSender: true))
            {
                Logger.LogWarning("Turn changed while completing remote action; stale turn-end request discarded. MessageUnitId={MessageUnitId}, CurrentUnitId={CurrentUnitId}", message.UnitId, Game?.Combat?.Turn?.UnitId);
                return;
            }

            lock (ActionLock)
            {
                Game.Combat.Turn.PlayersEndTurnInitialization.Add(message.PlayerId);
            }

            // AI turn is ended automatically by the game
            if (!Game.Combat.Turn.IsAI)
            {
                CombatInteraction.EndCombatTurn(message.UnitId);
            }

            TryEndTurn();
        }

        private void OnNotifyGlobalMapCommonPopupShown(long receivedFrom, NotifyGlobalMapCommonPopupShown message)
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapCommonPopup, message.PlayerId);
            var popup = Mapper.Map<NetworkGlobalMapCommonPopup>(message.Popup);
            UpdateGlobalMapCommonPopupUIState(popup);
        }

        private void OnClientTogglePauseOff(long receivedFrom, ClientTogglePauseOff message)
        {
            lock (ActionLock)
            {
                if (Game.ForcedPause != null && (Game.ForcedPause.Reason == NetworkForcedPauseReason.AreaLoading || Game.ForcedPause.IsLifting))
                {
                    Logger.LogWarning("Skipping un-pause request for AreaLoading pause");
                    return;
                }
            }

            TryEndForcedPause(message.PlayerId);
        }

        private void OnNotifyGlobalMapRecruitmentClosed(long receivedFrom, NotifyGlobalMapRecruitmentClosed message)
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapRecruitment);
        }

        private void OnNotifyGlobalMapRecruitmentShown(long receivedFrom, NotifyGlobalMapRecruitmentShown message)
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapRecruitment, message.PlayerId);
            UpdateGlobalMapRecruitmentUIState();
        }

        private void OnNotifyGlobalMapCrusadeArmyBuyLeaderClosed(long receivedFrom, NotifyGlobalMapCrusadeArmyBuyLeaderClosed message)
        {
            RemovePlayerFromTracker(Game.PlayersInGlobalMapCrusadeArmyBuyLeader, message.PlayerId);
            UpdateGlobalMapCrusadeArmyInfoUIStateAfterBuyLeader();
        }

        private void OnNotifyGlobalMapCrusadeArmySetLeaderClosed(long receivedFrom, NotifyGlobalMapCrusadeArmySetLeaderClosed message)
        {
            RemovePlayerFromTracker(Game.PlayersInGlobalMapCrusadeArmySetLeader, message.PlayerId);
            UpdateGlobalMapCrusadeArmyInfoUIStateAfterSetLeader();
        }

        private void OnNotifyGlobalMapCrusadeArmyInfoShown(long receivedFrom, NotifyGlobalMapCrusadeArmyInfoShown message)
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapCrusadeArmyInfo, message.PlayerId);

            UpdateGlobalMapCrusadeArmyInfoUIState();
        }

        private void OnNotifyGlobalMapCrusadeArmyMergeCartClosed(long receivedFrom, NotifyGlobalMapCrusadeArmyMergeCartClosed message)
        {
            RemovePlayerFromTracker(Game.PlayersInGlobalMapCrusadeArmyInfoMerge, message.PlayerId);
            UpdateGlobalMapCrusadeArmyInfoUIStateAfterMerge();
        }

        private void OnNotifyTacticalCombatInitializationConfirmed(long receivedFrom, NotifyTacticalCombatInitializationConfirmed tacticalCombatInitializationConfirmed)
        {
            Game.ArmyCombat.PlayersCombatInitialization.TryAdd(tacticalCombatInitializationConfirmed.PlayerId, true);

            Game.ArmyCombat.IsInitialized = TryConfirmTacticalCombatInitialization();
        }

        private void OnNotifyPolymorphicItemCreationRequested(long playerId, NotifyPolymorphicItemCreationRequested message)
        {
            var polymorphicItem = Mapper.Map<NetworkPolymorphicItem>(message.PolymorphicItem);
            GameInteraction.CreateAndEquipPolymorphicItem(polymorphicItem, createContext: false);

            // clients will receive 'NotifyPolymorphicItemCreated' as a result of polymorphic item creation
        }

        private void OnClientGameAutoPaused(long playerId, ClientGameAutoPaused message)
        {
            var pause = Mapper.Map<NetworkForcedPause>(message.Pause);
            if (IsPauseOnCooldown(pause.Reason))
            {
                ForceUnpause();
                return;
            }

            lock (ActionLock)
            {
                EnsureForcePaused(pause.Reason, pause.RemovalDelay);
                Game.ForcedPause.ReadyPlayers.Add(playerId);
            }
        }

        private void OnClientCharacterLevelingRequested(long playerId, ClientCharacterLevelingRequested message)
        {
            var levelingType = Mapper.Map<NetworkLevelingType>(message.Type);
            lock (ActionLock)
            {
                if (Game.Leveling == null)
                {
                    InitiateLeveling(message.UnitId, levelingType);
                    LevelingInteraction.StartLeveling(message.UnitId, levelingType);
                    SendLevelingStartedConfirmation();
                    return;
                }

                // force specific player to open correct leveling ui
                Logger.LogWarning("Leveling is already in progress. PlayerId={PlayerId}, UnitId={UnitId}, LevelingType={LevelingType}, RequestedUnitId={RequestedUnitId}, RequestedLevelingType={RequestedLevelingType}", playerId, Game.Leveling.UnitId, Game.Leveling.Type, message.UnitId, message.Type);
                SendLevelingStartedConfirmation(playerId);
            }
        }

        private async void OnRandomEncounterContextRequest(long receivedFrom, RandomEncounterContextRequest request)
        {
            var randomEncounterIndex = request.SleepPhase - 1;
            var timeout = Task.Delay(request.Timeout);
            await WaitWhileTrue(() => !timeout.IsCompleted && (Game.Rest == null || Game.Rest.RandomEncounters.Count <= randomEncounterIndex),
                $"Rest Random Encounter is not available yet. RequestedSleepPhase={request.SleepPhase}, RandomEncounterIndex={randomEncounterIndex}");

            var encounter = timeout.IsCompleted ? null : Game.Rest?.RandomEncounters[randomEncounterIndex];
            var response = new RandomEncounterContextResponse
            {
                Encounter = Mapper.Map<Networking.Messages.Contracts.NetworkRandomEncounter>(encounter)
            };
            Send(receivedFrom, response);
        }

        private void OnClientCombatTurnEndSynchronized(long playerId, ClientCombatTurnEndSynchronized message)
        {
            Game.Combat.Turn.PlayersEndTurnSynchronization.Add(playerId);
            TryEndTurn();
        }

        private void OnClientCombatTurnSynchronized(long playerId, ClientCombatTurnStartSynchronized message)
        {
            AddPlayerReadyStatus(PlayerTurnReadinessType.UnitSynchronization, playerId, message.UnitId);
            TryStartTurn();
        }

        private void OnClientCombatTurnStarted(long playerId, ClientCombatTurnStarted started)
        {
            AddPlayerReadyStatus(PlayerTurnReadinessType.Start, playerId, started.UnitId);

            TryStartTurn();
        }

        private void OnClientCombatInitializationCompleted(long playerId, ClientCombatInitializationCompleted message)
        {
            Game.Combat.PlayersFinishedStartupSequence.TryAdd(playerId, true);
        }

        private async void OnClientDialogStartRequested(long playerId, ClientDialogStartRequested message)
        {
            var dialog = Mapper.Map<NetworkDialog>(message.Dialog);
            var hasBeenStarted = await DialogInteraction.StartDialogAsync(dialog);
            if (hasBeenStarted)
            {
                return;
            }

            Logger.LogInformation("Host dialog is already in progress");
            var dialogStarted = new NotifyDialogStarted
            {
                Dialog = Mapper.Map<Networking.Messages.Contracts.NetworkDialog>(Game.DialogState.Dialog)
            };
            Send(dialogStarted);
        }

        private async void OnClientDialogCueAnswerSuggested(long playerId, ClientDialogCueAnswerSuggested message)
        {
            await WaitWhileTrue(() => Game.DialogState == null, "Waiting for dialog to initialize before suggesting cue answer");

            UpdateCueAnswerSuggestions(playerId, message.CueName, message.AnswerName);
        }

        private async void OnClientDialogCueWitnessed(long playerId, ClientDialogCueWitnessed message)
        {
            await WaitWhileTrue(() => Game.DialogState == null, "Waiting for dialog to initialize before witnessing cue");

            AddCueWitness(message.CueName, playerId);
            TryEnableDialogContinueButton();
        }

        private void OnNotifyLobbySyncStatusChanged(long receivedFrom, NotifySaveGameSyncStatusChanged message)
        {
            var status = Mapper.Map<NetworkLobbySyncStatus>(message.Status);
            UpdateLobbySyncStatus(message.PlayerId, status);

            TryStartSavedGame();
        }

        private void OnClientAreaLoaded(long receivedFrom, ClientAreaLoaded message)
        {
            lock (ActionLock)
            {
                EnsureForcePaused(NetworkForcedPauseReason.AreaLoading);
                Game.ForcedPause.ReadyPlayers.Add(receivedFrom);
            }

            TryEndForcedPause(receivedFrom);
        }

        private void OnPlayerConnected(long playerId)
        {
            try
            {
                Logger.LogInformation("New player connection. PlayerId={PlayerId}", playerId);

                lock (ActionLock)
                {
                    var existingPlayer = GetPlayer(playerId);
                    if (existingPlayer != null)
                    {
                        Logger.LogError("Player already exists. PlayerId={PlayerId}", playerId);
                        return;
                    }

                    var player = new NetworkPlayer(playerId);
                    Game.Players.Add(player);
                    var settings = GameInteraction.GetGameSettings();
                    if (settings != null)
                    {
                        settings.Multiplayer = SettingsService.GetSettings();
                    }

                    var message = new GameServerConnectionSucceeded
                    {
                        ClientPlayerId = playerId,
                        GameSettings = Mapper.Map<Networking.Messages.Contracts.NetworkGameSettings>(settings),
                        SessionSeed = Game.SessionSeed
                    };
                    Send(playerId, message);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to process new player connection");
            }
        }

        private void OnError(NetworkError error)
        {
            switch (error.Type)
            {
                case NetworkErrorType.UnreachableSignalingServer:
                    PlayerNotification.ShowModalMessage(WellKnownKeys.MultiplayerWindow.ExternalServers.Errors.UnreachableSignalingServer.Key, args: error.Reason);
                    break;
                case NetworkErrorType.ModConflict:
                    PlayerNotification.ShowModalMessage(WellKnownKeys.MultiplayerWindow.ExternalServers.Errors.ModConflict.Key);
                    break;
            }
        }

        private void OnExternalConnectivityUpdated(bool? isConnected, string code)
        {
            Game.Connectivity.External = new ExternalConnectivity
            {
                Code = code,
                Status = isConnected == null ? ExternalConnectivityStatus.Connecting
                    : isConnected.Value ? ExternalConnectivityStatus.Connected
                    : ExternalConnectivityStatus.Error
            };

            Logger.LogInformation("External connectivity updated. Code={Code}, Status={Status}", Game.Connectivity.External.Code, Game.Connectivity.External.Status);
            OnGameConnectivityUpdated?.Invoke(Game.Connectivity);
        }

        private void OnLocalServerStarted(EndPoint endpoint)
        {
            var hostPlayer = new NetworkPlayer(NetworkConstants.HostPlayerId)
            {
                Name = SettingsService.GetSettings().PlayerName,
                ContentState = GameInteraction.GetInstalledContent(),
                IsHost = true
            };

            hostPlayer.Color = GetPlayerColor(hostPlayer.Name);

            Game.Players.Add(hostPlayer);

            Game.Connectivity = new GameConnectivity
            {
                Endpoint = endpoint
            };

            foreach (var character in Game.Characters)
            {
                character.Owner = hostPlayer;
            }

            var enforcedSettings = GetEnforcedGameSettings();
            GameInteraction.ApplyGameSettings(enforcedSettings);

            OnConnected?.Invoke(Game.Connectivity);
            InvokeOnPlayersChanged();
            Logger.LogInformation("Server has been started. DLCs={DLCs}, Mods={Mods}", hostPlayer.ContentState.DLCs.Count, hostPlayer.ContentState.Mods.Count);
        }

        private void OnPlayerDisconnected(long playerId)
        {
            lock (ActionLock)
            {
                var removedPlayer = CleanupPlayer(playerId);
                if (removedPlayer == null)
                {
                    return;
                }

                InvokeOnPlayersChanged();
                var playersChanged = CreateNotifyLobbyPlayersChanged();
                _networkHost.BroadcastExcept(playerId, playersChanged);

                RefreshUIOnPlayerDisconnect(removedPlayer.Id);

                ShowPlayerDisconnectedMessage(removedPlayer);

                TryEnableDialogContinueButton();

                TryEndForcedPause(Game.LocalPlayerId);
                TryCompleteRealTimeAbort();
                HandleRealTimeRepairPeerDisconnected(playerId);
            }
        }

        private void HandleRealTimeRepairPeerDisconnected(long playerId)
        {
            if (!_realTimeRepairStage.HasValue || Game?.RealTimeCombat == null)
            {
                return;
            }
            Game.RealTimeCombat.RequiredPlayers.Remove(playerId);
            Game.RealTimeCombat.ReadyPlayers.Remove(playerId);
            if (!Game.RealTimeCombat.RequiredPlayers.IsSubsetOf(Game.RealTimeCombat.ReadyPlayers))
            {
                return;
            }
            switch (_realTimeRepairStage.Value)
            {
                case RealTimeRepairStage.Prepare:
                    _ = StartRealTimeRepairVerificationAsync();
                    break;
                case RealTimeRepairStage.Verify:
                    if (_realTimeRepairHasMismatch) StartRealTimeRepairApply();
                    else CompleteRealTimeRepair();
                    break;
                case RealTimeRepairStage.Apply:
                    CompleteRealTimeRepair();
                    break;
                case RealTimeRepairStage.Fallback:
                    CommitRealTimeRepairFallback();
                    break;
                case RealTimeRepairStage.Resume:
                    TryFinalizeRealTimeRepairResume();
                    break;
            }
        }

        private void OnClientGameServerConnectionConfirmed(long playerId, ClientGameServerConnectionConfirmed message)
        {
            try
            {
                lock (ActionLock)
                {
                    var existingPlayer = GetPlayer(playerId);
                    if (existingPlayer == null)
                    {
                        Logger.LogError("Can't process player name update because player doesn't exist. PlayerId={PlayerId}, Name={Name}", playerId, message.PlayerName);
                        return;
                    }

                    if (string.IsNullOrEmpty(message.PlayerName))
                    {
                        Logger.LogError("Can't process player name update because player name is missing. PlayerId={PlayerId}, Name={Name}", playerId, message.PlayerName);
                        return;
                    }

                    existingPlayer.Name = message.PlayerName;
                    existingPlayer.ContentState = Mapper.Map<NetworkContentState>(message.ContentState);

                    existingPlayer.Color = GetPlayerColor(existingPlayer.Name);

                    var host = GetHost();
                    existingPlayer.ContentState.DiscrepantDLCs = CompareDLCs(host.ContentState, existingPlayer.ContentState);
                    existingPlayer.ContentState.DiscrepantMods = CompareMods(host.ContentState, existingPlayer.ContentState);
                    Logger.LogInformation("Player content has been checked. PlayerId={PlayerId}, DiscrepantDLCs={DiscrepantDLCs}, DiscrepantMods={DiscrepantMods}", existingPlayer.Id, existingPlayer.ContentState.DiscrepantDLCs.Count, existingPlayer.ContentState.DiscrepantMods.Count);

                    var playersChanged = CreateNotifyLobbyPlayersChanged();
                    Send(playersChanged);

                    var lobbyCharactersChanged = new NotifyLobbyCharactersChanged
                    {
                        Title = Game.StartUp?.Title,
                        Characters = Mapper.Map<List<Networking.Messages.Contracts.NetworkCharacter>>(Game.Characters)
                    };
                    Send(playerId, lobbyCharactersChanged);

                    if (existingPlayer.ContentState.RealTimeProtocolVersion == NetworkRealTimeCombatState.ProtocolVersion)
                    {
                        SendRealTimePreference(playerId);
                    }

                    InvokeOnPlayersChanged();

                    ShowPlayerConnectedMessage(existingPlayer);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to handle player name response");
                throw;
            }
        }

        private NetworkColor GetPlayerColor(string playerName)
        {
            var playersWithColor = Game.Players.Where(x => x.Color != null).ToList();
            var usedColors = playersWithColor.Select(x => x.Color).ToHashSet();
            var colorOrder = GetColorPreferences(playerName, WellKnownColors.PlayerColors.All.Count);

            foreach (var index in colorOrder)
            {
                if (!usedColors.Contains(WellKnownColors.PlayerColors.All[index]))
                {
                    return WellKnownColors.PlayerColors.All[index];
                }
            }

            var playersByColor = playersWithColor.GroupBy(p => p.Color).ToDictionary(x => x.Key, x => x.Count());
            var leastUsedColor = colorOrder.OrderBy(c => playersByColor[WellKnownColors.PlayerColors.All[c]]).First();
            return WellKnownColors.PlayerColors.All[leastUsedColor];
        }

        private int[] GetColorPreferences(string playerName, int colorCount)
        {
            var preferences = Enumerable.Range(0, colorCount).ToArray();

            var seed = _hashService.Murmur3(playerName);
            var random = new System.Random(seed);

            for (var i = preferences.Length - 1; i > 0; i--)
            {
                var swapIndex = random.Next(i + 1);
                (preferences[i], preferences[swapIndex]) = (preferences[swapIndex], preferences[i]);
            }

            return preferences;
        }

        private List<NetworkDiscrepantDLC> CompareDLCs(NetworkContentState hostState, NetworkContentState clientState)
        {
            var discrepantDLCs = new List<NetworkDiscrepantDLC>();
            var clientDLCs = clientState.DLCs.ToList();
            foreach (var hostDlc in hostState.DLCs)
            {
                var clientDlc = clientDLCs.FirstOrDefault(d => string.Equals(d.Id, hostDlc.Id, StringComparison.OrdinalIgnoreCase));
                NetworkDiscrepancyReason? reason = null;
                if (clientDlc == null || hostDlc.IsAvailable && !clientDlc.IsAvailable)
                {
                    reason = NetworkDiscrepancyReason.Missing;
                }
                else if (!hostDlc.IsAvailable && clientDlc.IsAvailable)
                {
                    reason = NetworkDiscrepancyReason.Extra;
                }

                if (reason != null)
                {
                    discrepantDLCs.Add(new NetworkDiscrepantDLC(hostDlc, reason.Value));
                }

                if (clientDlc != null)
                {
                    clientDLCs.Remove(clientDlc);
                }
            }

            var availableLeftovers = clientDLCs.Where(x => x.IsAvailable).Select(x => new NetworkDiscrepantDLC(x, NetworkDiscrepancyReason.Extra));
            discrepantDLCs.AddRange(availableLeftovers);

            return discrepantDLCs;
        }

        private List<NetworkDiscrepantMod> CompareMods(NetworkContentState hostState, NetworkContentState clientState)
        {
            var discrepantMods = new List<NetworkDiscrepantMod>();
            var clientMods = clientState.Mods.ToList();
            foreach (var hostMod in hostState.Mods)
            {
                if (AudioOnlyModCompatibility.IsLocalAudioMod(hostMod.Id))
                {
                    Logger.LogInformation("Ignoring local audio-only mod difference. ModId={ModId}, HostEnabled={HostEnabled}", hostMod.Id, hostMod.IsEnabled);
                    clientMods.RemoveAll(m => string.Equals(m.Id, hostMod.Id, StringComparison.OrdinalIgnoreCase));
                    continue;
                }

                var clientMod = clientMods.FirstOrDefault(m => string.Equals(m.Id, hostMod.Id, StringComparison.OrdinalIgnoreCase));
                NetworkDiscrepancyReason? reason = null;
                if (clientMod == null)
                {
                    if (hostMod.IsEnabled)
                    {
                        reason = NetworkDiscrepancyReason.Missing;
                    }
                }
                else if (hostMod.IsEnabled && !clientMod.IsEnabled)
                {
                    reason = NetworkDiscrepancyReason.Disabled;
                }
                else if (!hostMod.IsEnabled && clientMod.IsEnabled)
                {
                    reason = NetworkDiscrepancyReason.Extra;
                }
                else if (hostMod.IsEnabled && clientMod.IsEnabled && !string.Equals(clientMod.Version, hostMod.Version, StringComparison.OrdinalIgnoreCase))
                {
                    reason = NetworkDiscrepancyReason.VersionMismatch;
                }

                if (reason != null)
                {
                    discrepantMods.Add(new NetworkDiscrepantMod(hostMod.Id, hostMod.Type, clientMod?.Version, hostMod.Version, reason.Value));
                }

                if (clientMod != null)
                {
                    clientMods.Remove(clientMod);
                }
            }

            var enabledLeftovers = clientMods.Where(x => x.IsEnabled && !AudioOnlyModCompatibility.IsLocalAudioMod(x.Id)).Select(x => new NetworkDiscrepantMod(x.Id, x.Type, x.Version, null, NetworkDiscrepancyReason.Extra)).ToList();
            discrepantMods.AddRange(enabledLeftovers);

            return discrepantMods;
        }

        private NotifyLobbyPlayersChanged CreateNotifyLobbyPlayersChanged()
        {
            var playersChanged = new NotifyLobbyPlayersChanged
            {
                Players = Mapper.Map<List<Networking.Messages.Contracts.NetworkPlayer>>(GetPlayers())
            };
            return playersChanged;
        }

        private NetworkGameSettings GetEnforcedGameSettings()
        {
            var settings = new NetworkGameSettings
            {
                TurnBased = new NetworkTurnBasedSettngs
                {
                    IsTurnBasedModeEnabled = true,
                    AutoStopAfterFirstMoveAction = false,
                    AutoEndTurn = false,
                },
                Main = new NetworkGameMainSettings
                {
                    LootInCombat = false,
                    QuickMovement = false
                },
                Autopause = new NetworkAutopauseSettings
                {
                    PauseOnTrapDetected = true,
                    PauseOnSpellcastInterrupted = Kingmaker.Settings.EntitiesType.None,
                    PauseOnSpellcastStarted = Kingmaker.Settings.EntitiesType.None,
                    // everything else is false for auto-pause
                },
                // tutorial is disabled because most of tutorial popups pause the game
                Tutorial = new NetworkTutorialSettings()
            };

            return settings;
        }

        private void AddCueWitness(string cueName, long playerId)
        {
            if (Game.DialogState == null)
            {
                Logger.LogError("Trying to add witness to null dialog. CueName={CueName}, PlayerId={PlayerId}", cueName, playerId);
                return;
            }

            lock (ActionLock)
            {
                Game.DialogState.CueViews.AddOrUpdate(cueName, (key) => new HashSet<long>([playerId]), (key, existing) =>
                {
                    existing.Add(playerId);
                    return existing;
                });
            }

            Logger.LogInformation("Cue witness has been added. CueName={CueName}, PlayerId={PlayerId}", cueName, playerId);
        }

        private bool TryConfirmTacticalCombatInitialization()
        {
            if (Game.ArmyCombat.IsInitialized)
            {
                return false;
            }

            var everyoneIsReady = Game.ArmyCombat.PlayersCombatInitialization.Count(x => x.Value) >= GetSyncedPlayersCount();
            Logger.LogInformation("Checking crusade army combat initialization. IsReady={IsReady}", everyoneIsReady);
            return everyoneIsReady;
        }

        private List<NetworkPlayer> GetPlayersWhoHaveNotSeenCueYet(string cueName)
        {
            if (Game.DialogState == null)
            {
                Logger.LogWarning("Trying to get cue players, but dialog is null. CueName={CueName}", cueName);
                return [];
            }

            if (!Game.DialogState.CueViews.TryGetValue(cueName ?? string.Empty, out var cueViews))
            {
                Logger.LogWarning("Specified cue doesn't exist in the views history. CueName={CueName}", cueName);
                return [];
            }

            var players = GetSyncedPlayers().Where(p => !cueViews.Contains(p.Id)).ToList();
            return players;
        }

        private void TryEnableDialogContinueButton()
        {
            if (Game.DialogState == null)
            {
                Logger.LogWarning("Unable to enable continue button because current dialog is null");
                return;
            }

            var currentCue = Game.DialogState.CurrentCueName;
            var missingPlayers = GetPlayersWhoHaveNotSeenCueYet(currentCue);
            if (missingPlayers.Count > 0)
            {
                Logger.LogInformation("Cannot proceed with dialog yet. CurrentCue={CurrentCue}, MissingPlayers={MissingPlayers}", currentCue, string.Join(";", missingPlayers.Select(x => x.Name)));
                return;
            }

            Logger.LogInformation("All players have witnessed current cue. CueName={CueName}", currentCue);
            DialogInteraction.SetDialogContinueButtonState(true);
        }

        private List<NetworkPlayer> GetPlayersNotReadyToResume(long requestedByPlayerId)
        {
            var result = new List<NetworkPlayer>();
            var players = GetSyncedPlayers();
            foreach (var player in players)
            {
                if (player.Id == requestedByPlayerId
                    || Game.ForcedPause.ReadyPlayers.Contains(player.Id)
                    || Game.ForcedPause.Reason != NetworkForcedPauseReason.AreaLoading && player.Id == Game.LocalPlayerId && !GameInteraction.IsPaused)
                {
                    continue;
                }

                result.Add(player);
            }

            return result;
        }

        private bool TryEndForcedPause(long requestedByPlayerId)
        {
            try
            {
                Logger.LogInformation("Checking if forced pause could be removed. PauseIsNull={PauseIsNull}, IsLifting={IsLifting}, RequestedByPlayer={RequestedByPlayer}", Game.ForcedPause == null, Game.ForcedPause?.IsLifting, requestedByPlayerId);

                if (Game.ForcedPause == null)
                {
                    return true;
                }

                if (Game.ForcedPause.IsLifting)
                {
                    return false;
                }

                lock (ActionLock)
                {
                    var missingPlayer = GetPlayersNotReadyToResume(requestedByPlayerId);
                    if (missingPlayer.Any())
                    {
                        Logger.LogInformation("Not everyone is ready, forced pause will remain. MissingPlayers={MissingPlayers}", missingPlayer.Select(x => x.Name));
                        return false;
                    }

                    var removalDelay = Game.ForcedPause.RemovalDelay;
                    var delay = removalDelay.HasValue ? Task.Delay(removalDelay.Value) : Task.CompletedTask;
                    Game.ForcedPause.IsLifting = true;
                    if (Game.ForcedPause.Reason == NetworkForcedPauseReason.AreaLoading)
                    {
                        var message = new NotifyAreaLoadingCompleted
                        {
                            AreaSeed = Game.CurrentArea.Seed
                        };
                        Send(message);
                    }

                    Logger.LogInformation("Forced pause will be lifted soon. Reason={Reason}, RemovalDelay={RemovalDelay}", Game.ForcedPause.Reason, removalDelay.GetValueOrDefault());
                    delay.ContinueWith(x =>
                    {
                        try
                        {
                            GlobalMapInteraction.EnableInteractions();

                            if (Game.ForcedPause == null)
                            {
                                Logger.LogWarning("Previous forced pause lifter has been skipped due to null forced pause. Most likely game was quick loaded");
                                return;
                            }

                            var message = new NotifyGamePauseEnded();
                            Send(message);

                            Game.ForcedPause = null;
                            GameInteraction.SetPause(false);
                            Logger.LogInformation("Forced pause has been lifted");
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "Error while unpausing after delay");
                            throw;
                        }
                    });
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to end forced pause");
                throw;
            }
        }

        private void TryStartSavedGame()
        {
            lock (ActionLock)
            {
                var canStart = Game.Stage == NetworkLobbyStage.PreparingToStart && Game.Players.All(p => p.LobbySyncStatus == NetworkLobbySyncStatus.Succeed);

                if (canStart)
                {
                    Logger.LogInformation("Everyone is synced, game can be started. LobbyStage={LobbyStage}, IsNewGameSequence={IsNewGameSequence}", Game.Stage, Game.StartUp.IsNewGameSequence);
                    Send(new NotifyGameStarted());
                    OnGameStarted?.Invoke();

                    if (Game.StartUp.IsNewGameSequence)
                    {
                        StartNewGameSequence();
                        return;
                    }

                    LoadSavedGame();
                }
            }
        }

        private void SendLevelingStartedConfirmation(long? playerId = null)
        {
            var message = new NotifyLevelingStarted
            {
                UnitId = Game.Leveling.UnitId,
                Type = Game.Leveling.Type.ToString()
            };
            if (playerId.HasValue)
            {
                Send(playerId.Value, message);
                return;
            }

            Send(message);
        }

        private Dictionary<long, List<string>> GeCombatUnitsDiscrepancy()
        {
            var initial = Game.Combat.InitialUnitsInCombat;

            var unitsToAdd = new Dictionary<long, List<string>>();
            foreach (var player in initial.Keys)
            {
                unitsToAdd[player] = [];
            }

            var unitPlayers = new Dictionary<string, HashSet<long>>();

            foreach (var (playerId, units) in initial)
            {
                foreach (var unit in units)
                {
                    if (!unitPlayers.TryGetValue(unit, out var players))
                    {
                        players = [];
                        unitPlayers[unit] = players;
                    }

                    players.Add(playerId);
                }
            }

            var playerCount = initial.Count;

            foreach (var (unit, players) in unitPlayers)
            {
                if (players.Count == playerCount)
                {
                    continue;
                }

                foreach (var playerId in initial.Keys)
                {
                    if (!players.Contains(playerId))
                    {
                        unitsToAdd[playerId].Add(unit);
                    }
                }
            }

            return unitsToAdd;
        }

        private void UpdateCueAnswerSuggestions(long playerId, string cueName, string answerName)
        {
            Game.DialogState.AnswerSuggestions.AddOrUpdate(playerId, answerName, (key, existing) =>
            {
                return answerName;
            });

            List<NetworkDialogAnswerSuggestion> suggestions = [.. Game.DialogState.AnswerSuggestions.GroupBy(x => x.Value, x => x.Key).Select(x => new NetworkDialogAnswerSuggestion { AnswerName = x.Key, Players = [.. x] })];

            var syncedPlayers = GetSyncedPlayers();
            DialogInteraction.MarkSuggestedCueAnswers(syncedPlayers, suggestions);

            var cueAnswerSuggested = new NotifyDialogCueAnswerSuggested
            {
                Dialog = Mapper.Map<Networking.Messages.Contracts.NetworkDialog>(Game.DialogState.Dialog),
                CueName = cueName,
                Suggestions = Mapper.Map<List<Networking.Messages.Contracts.NetworkDialogAnswerSuggestion>>(suggestions),
            };
            Send(cueAnswerSuggested);
        }

        private void TryStartTurn()
        {
            try
            {
                Logger.LogInformation("Checking if turn could be started. Round={Round}, UnitId={UnitId}", Game.Combat.Round, Game.Combat.Turn?.UnitId);

                lock (ActionLock)
                {
                    if (Game.Combat.Turn == null
                        || (Game.Combat.Turn.Stage != NetworkCombatTurnStage.Starting && Game.Combat.Turn.Stage != NetworkCombatTurnStage.StartSynchronization))
                    {
                        Logger.LogWarning("Turn is not ready to be started yet. TurnStatus={TurnStatus}", Game.Combat.Turn?.Stage);
                        return;
                    }

                    var desyncedPlayers = Game.Combat.PlayersNextTurnInitialization.Where(k => !string.Equals(k.Key, Game.Combat.Turn.UnitId, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (desyncedPlayers.Count > 0)
                    {
                        foreach (var desynced in desyncedPlayers)
                        {
                            Game.Combat.PlayersNextTurnInitialization.TryRemove(desynced.Key, out _);
                        }

                        var players = desyncedPlayers.SelectMany(x => x.Value).Distinct().ToList();
                        Logger.LogWarning("Players have started different turn. Initiating recovering. Players={Players}", desyncedPlayers.ToDictionary(x => x.Key, x => x.Value.ToList()));
                        foreach (var playerId in players)
                        {
                            var player = GetPlayer(playerId);
                            if (player == null || player.IsHost)
                            {
                                continue;
                            }

                            PlayerNotification.AddCombatText(WellKnownKeys.GameNotifications.Combat.Turn.HostOrderDesync.Key, CombatTextSeverity.Debug, player.Name);

                            var desyncedTurnStartMessage = new NotifyInvalidCombatTurnStarted
                            {
                                UnitId = Game.Combat.Turn.UnitId,
                            };
                            Send(playerId, desyncedTurnStartMessage);
                        }

                        return;
                    }

                    var notInitializedPlayers = GetMissingPlayers(Game.Combat.Turn.UnitId, Game.Combat.PlayersNextTurnInitialization);
                    if (notInitializedPlayers.Count > 0)
                    {
                        Logger.LogInformation("Unable to start turn due to missing players turn initialization. MissingPlayersCount={MissingPlayersCount}, MissingPlayers={MissingPlayers}", notInitializedPlayers.Count, string.Join(";", notInitializedPlayers.Select(p => p.Name)));
                        return;
                    }

                    if (Game.Combat.Turn.Stage == NetworkCombatTurnStage.Starting)
                    {
                        SetCombatTurnStage(NetworkCombatTurnStage.StartSynchronization);
                        var combatState = CombatInteraction.GetCombatState();
                        var syncMessage = new NotifyCombatTurnStartSynchronizationRequired
                        {
                            CombatState = Mapper.Map<Networking.Messages.Contracts.NetworkCombatState>(combatState),
                            TriggeredAreaEffects = Mapper.Map<List<Networking.Messages.Contracts.NetworkAreaEffect>>(Game.Combat.TriggeredAreaEffects)
                        };
                        Game.Combat.TriggeredAreaEffects.Clear();
                        Send(syncMessage);
                    }

                    var notSynchronizedPlayers = GetMissingPlayers(Game.Combat.Turn.UnitId, Game.Combat.PlayersNextTurnSynchronization);
                    if (notSynchronizedPlayers.Count > 0)
                    {
                        Logger.LogWarning("Unable to start turn due to missing players turn synchronization. MissingPlayers={MissingPlayers}", string.Join(";", notSynchronizedPlayers.Select(p => p.Name)));
                        return;
                    }

                    Game.Combat.PlayersNextTurnInitialization.Clear();
                    Game.Combat.PlayersNextTurnSynchronization.Clear();

                    ValueGenerator.ResetSeededGenerators(IdentifierLifetime.CombatTurn);
                    Game.Combat.Turn.Seed = CreateRandomSeed();

                    var message = new NotifyCombatTurnStarted
                    {
                        Round = Game.Combat.Round,
                        UnitId = Game.Combat.Turn.UnitId,
                        Seed = Game.Combat.Turn.Seed.Value,
                    };

                    Send(message);
                    SetCombatTurnStage(NetworkCombatTurnStage.Playing);
                }

                CombatInteraction.StartTurnBasedCombatTurn(Game.Combat.Turn.UnitId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error while trying to start turn");
                throw;
            }
        }

        private void TryEndTurn()
        {
            try
            {
                lock (ActionLock)
                {
                    Logger.LogInformation("Checking if turn could be ended. Round={Round}, UnitId={UnitId}, IsAI={IsAI}", Game.Combat.Round, Game.Combat.Turn.UnitId, Game.Combat.Turn.IsAI);
                    var allPlayers = GetSyncedPlayersCount();

                    var initializedPlayers = Game.Combat.Turn.PlayersEndTurnInitialization.Count;
                    if (initializedPlayers < allPlayers)
                    {
                        Logger.LogInformation("Can't end turn due to missing player turn end initialization. ReadyPlayers={ReadyPlayers}, RequiredPlayers={RequiredPlayers}", initializedPlayers, allPlayers);
                        return;
                    }

                    if (Game.Combat.Turn.Stage == NetworkCombatTurnStage.Ending)
                    {
                        SetCombatTurnStage(NetworkCombatTurnStage.EndSynchronization);
                        var units = CombatInteraction.GetUnitsInCombat();
                        var turnEndSyncMessage = new NotifyCombatTurnEndSynchronizationRequired
                        {
                            Units = Mapper.Map<List<Networking.Messages.Contracts.NetworkUnit>>(units)
                        };
                        Send(turnEndSyncMessage);
                    }

                    var turnEndSyncedPlayers = Game.Combat.Turn.PlayersEndTurnSynchronization.Count;
                    if (turnEndSyncedPlayers < allPlayers)
                    {
                        Logger.LogInformation("Can't end turn due to missing player turn end synchronization. ReadyPlayers={ReadyPlayers}, RequiredPlayers={RequiredPlayers}", turnEndSyncedPlayers, allPlayers);
                        return;
                    }

                    Logger.LogInformation("Turn has been ended");
                    var turnEndMessage = new NotifyCombatTurnEnded();
                    Send(turnEndMessage);

                    // Game calls 'turn end' every tick, no need for extra calls
                    SetCombatTurnStage(NetworkCombatTurnStage.Ended);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error while trying to end turn");
                throw;
            }
        }
    }
}
