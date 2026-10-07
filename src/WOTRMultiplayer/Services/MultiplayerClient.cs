using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Threading;
using AutoMapper;
using Kingmaker.UI.Kingdom;
using Kingmaker.Utility;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Abstractions;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Abstractions.GameInteraction.CombatLog;
using WOTRMultiplayer.Abstractions.IO;
using WOTRMultiplayer.Abstractions.Random;
using WOTRMultiplayer.Abstractions.Settings;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Area;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.Crusades;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Connectivity;
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
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Abstractions;
using WOTRMultiplayer.Networking.Configuration;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Networking.Messages.Lobby;
using WOTRMultiplayer.Networking.Messages.Requests;
using WOTRMultiplayer.Services.GameInteraction.Contexts;

namespace WOTRMultiplayer.Services
{
    public class MultiplayerClient : MultiplayerActorBase, IMultiplayerClient
    {
        private readonly INetworkClientConnection _networkClient;
        private readonly SemaphoreSlim _realTimeSnapshotGate = new(1, 1);
        private readonly SemaphoreSlim _turnEndSynchronizationGate = new(1, 1);
        private readonly RealTimeRuleRollMailbox _realTimeRuleRolls = new();
        private readonly RealTimeEntityIdMailbox _realTimeEntityIds = new();
        private const int RealTimeRuleRollTimeoutMs = 2500;
        private readonly object _realTimeCommandSequenceLock = new();
        private long _realTimeCommandEpoch;
        private long _nextRealTimeCommandSequence;
        private long _realTimePauseRequestEpoch;
        private long _nextRealTimePauseRequestSequence;
        private readonly ConcurrentDictionary<string, ConcurrentQueue<NetworkAIAction>> _realTimeAIActions = new(StringComparer.Ordinal);
        private readonly SortedDictionary<long, NetworkAIAction> _pendingRealTimeAIChoices = [];
        private readonly object _realTimeAIChoiceLock = new();
        private const int MaxPendingRealTimeAIChoices = 128;
        private const int RealTimeAIChoiceGapTimeoutMs = 5000;
        private long _realTimeAISequenceEpoch;
        private long _lastRealTimeAISequence;
        private long _realTimeAIChoiceGapExpectedSequence;
        private long _realTimeAIChoiceGeneration;
        private NetworkGame _lastRealTimeDeathSession;
        private long _lastRealTimeDeathEpoch;
        private int _lastRealTimeDeathSaveSeed;
        private readonly HashSet<long> _appliedRealTimeDeathSequences = [];
        private NetworkRealTimeCombatState _localCriticalFaultState;
        private long _localCriticalFaultEpoch;
        private volatile bool _disconnectedDuringRealTimeCombat;
        private NetworkGame _turnEndSynchronizationSession;
        private long _lastTurnEndSynchronizationSequence;
        private volatile bool _turnEndSynchronizationFailed;

        public Action OnNetworkError { get; set; }

        public Action<NetworkCharacter> OnCharacterOwnerChanged { get; set; }

        public bool IsActive => _networkClient.IsActive;

        public bool IsConnecting => _networkClient.IsConnecting;

        public bool IsRealTimeCombatActive => IsActive && Game?.Combat?.Stage == NetworkCombatStage.Running
            && Game.RealTimeCombat?.Mode == CombatMode.RealTimeWithPause;

        public bool IsRealTimeCombatBarrierActive => IsActive && Game?.Combat?.Stage == NetworkCombatStage.Running
            && Game.RealTimeCombat != null && Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable;

        public bool IsSavingBlockedByCombatSync => _disconnectedDuringRealTimeCombat || _turnEndSynchronizationFailed
            || IsActive && Game?.RealTimeCombat != null
                && (Game.RealTimeCombat.BlocksSaving
                    || Game.Combat?.Stage == NetworkCombatStage.Running
                        && Game.RealTimeCombat.Mode == CombatMode.RealTimeWithPause);

        public bool IsSavingBlockedAfterRealTimeDisconnect => _disconnectedDuringRealTimeCombat;

        public int ResolveRealTimeDiceRoll(string key, Func<int> localRoll)
        {
            // Advance the deterministic generator too; only the host's result enters the rule.
            var localResult = localRoll();
            if (!IsRealTimeCombatActive || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable)
                return localResult;
            var epoch = Game.RealTimeCombat.Epoch;
            if (_realTimeRuleRolls.TryConsume(epoch, key, RealTimeRuleRollTimeoutMs,
                out var sequence, out var hostResult))
            {
                if (hostResult != localResult)
                {
                    Logger.LogWarning("Client combat roll differed from host. Epoch={Epoch}, Sequence={Sequence}, Key={Key}, Local={Local}, Host={Host}",
                        epoch, sequence, key, localResult, hostResult);
                    EnterLocalCriticalFaultBarrier(epoch);
                    Send(new ClientRealTimeRuleRollFault
                    {
                        Epoch = epoch, Key = key, Sequence = sequence, Reason = "local-result-mismatch"
                    });
                }
                return hostResult;
            }
            Logger.LogError("Authoritative combat roll unavailable; requesting paused repair. Epoch={Epoch}, Sequence={Sequence}, Key={Key}",
                epoch, sequence, key);
            if (IsRealTimeCombatActive && Game.RealTimeCombat.Epoch == epoch)
            {
                EnterLocalCriticalFaultBarrier(epoch);
                Send(new ClientRealTimeRuleRollFault
                {
                    Epoch = epoch, Key = key, Sequence = sequence, Reason = "host-roll-timeout"
                });
            }
            return localResult;
        }

        public string ResolveRealTimeEntityId(string key, Func<string> localId)
        {
            // Keep deterministic local counters advancing even though the host ID wins.
            var local = localId();
            if (!IsRealTimeCombatActive || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable)
                return local;
            var epoch = Game.RealTimeCombat.Epoch;
            if (_realTimeEntityIds.TryConsume(epoch, key, RealTimeRuleRollTimeoutMs,
                out var sequence, out var hostId))
            {
                if (!string.Equals(hostId, local, StringComparison.Ordinal))
                {
                    Logger.LogWarning("Entity ID differed from host. Epoch={Epoch}, Sequence={Sequence}, Key={Key}, Local={Local}, Host={Host}",
                        epoch, sequence, key, local, hostId);
                }
                return hostId;
            }
            Logger.LogError("Authoritative entity ID unavailable; requesting protected fallback. Epoch={Epoch}, Sequence={Sequence}, Key={Key}",
                epoch, sequence, key);
            if (IsRealTimeCombatActive && Game.RealTimeCombat.Epoch == epoch)
            {
                EnterLocalCriticalFaultBarrier(epoch);
                Send(new ClientRealTimeEntityIdFault
                {
                    Epoch = epoch, Key = key, Sequence = sequence, Reason = "host-id-timeout"
                });
            }
            return local;
        }

        private void EnterLocalCriticalFaultBarrier(long epoch)
        {
            if (!IsRealTimeCombatActive || Game.RealTimeCombat.Epoch != epoch
                || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable)
                return;
            _localCriticalFaultState = Game.RealTimeCombat;
            _localCriticalFaultEpoch = epoch;
            Game.RealTimeCombat.Phase = CombatSynchronizationPhase.Repairing;
            try
            {
                GameInteraction.SetPause(true);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to pause client after a critical real-time fault. Epoch={Epoch}", epoch);
            }
        }

        protected override void ResetRealTimeRuleRolls()
        {
            _realTimeRuleRolls.Reset();
            _realTimeEntityIds.Reset();
        }

        private NetworkLobbyStage Status => Game?.Stage ?? NetworkLobbyStage.None;

        public bool IsInLobby => IsActive && Status == NetworkLobbyStage.Lobby;

        protected override bool HasControlOverUI => false;

        public MultiplayerClient(
            ILogger<MultiplayerClient> logger,
            IGameInteractionService gameInteractionService,
            ILevelingInteractionService levelingInteractionService,
            IPlayerNotificationService playerNotificationService,
            IDialogInteractionService dialogInteractionService,
            IGlobalMapInteractionService globalMapInteractionService,
            IPingInteractionService pingInteractionService,
            ICombatInteractionService combatInteractionService,
            IMultiplayerSettingsService multiplayerSettingsProvider,
            IFileSystemService fileSystemService,
            INetworkClientConnection networkClient,
            IValueGenerator valueGenerator,
            IMapper mapper)
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
                  networkClient)
        {
            _networkClient = networkClient;

            SetupNetworkMessageHandlers();
        }

        public void Connect(string address, int port)
        {
            _realTimeRuleRolls.Reset();
            _realTimeEntityIds.Reset();
            var settings = SettingsService.GetSettings();
            _networkClient.ConnectAsync(address, port, settings.NetworkAwaiterTimeout).ConfigureAwait(false);
        }

        public void Connect(string code, string password, Entities.Connectivity.ExternalServer externalServer)
        {
            _realTimeRuleRolls.Reset();
            _realTimeEntityIds.Reset();
            var settings = SettingsService.GetSettings();
            var externalServerConfiguration = new ExternalServerConfiguration
            {
                AutoCreateGame = false,
                Port = settings.PeerToPeerPort,
                Server = Mapper.Map<Networking.Configuration.ExternalServer>(externalServer)
            };

            _networkClient.ConnectAsync(code, password, externalServerConfiguration, settings.NetworkAwaiterTimeout);
        }

        public void Reset()
        {
            Logger.LogInformation("Resetting");
            _realTimeRuleRolls.Reset();
            _realTimeEntityIds.Reset();
            _disconnectedDuringRealTimeCombat = false;
            _turnEndSynchronizationFailed = false;
            _turnEndSynchronizationSession = null;
            _lastTurnEndSynchronizationSequence = 0;
            _localCriticalFaultState = null;

            Game = null;

            lock (_realTimeCommandSequenceLock)
            {
                _realTimeCommandEpoch = 0;
                _nextRealTimeCommandSequence = 0;
                _realTimePauseRequestEpoch = 0;
                _nextRealTimePauseRequestSequence = 0;
            }

            _networkClient?.Reset();
            ResetRealTimeAIChoices(0);
        }

        private void ResetRealTimeAIChoices(long epoch)
        {
            lock (_realTimeAIChoiceLock)
            {
                _realTimeAIActions.Clear();
                _pendingRealTimeAIChoices.Clear();
                _realTimeAISequenceEpoch = epoch;
                _lastRealTimeAISequence = 0;
                _realTimeAIChoiceGapExpectedSequence = 0;
                _realTimeAIChoiceGeneration++;
            }
        }

        public bool TryConsumeAuthoritativeRealTimeAIAction(string unitId, out NetworkAIAction action)
        {
            action = null;
            lock (_realTimeAIChoiceLock)
            {
                return IsRealTimeCombatActive && Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Stable
                    && _realTimeAISequenceEpoch == Game.RealTimeCombat.Epoch
                    && !string.IsNullOrWhiteSpace(unitId)
                    && _realTimeAIActions.TryGetValue(unitId, out var queue)
                    && queue.TryDequeue(out action);
            }
        }

        public void ReportRealTimeAIActionFault(string unitId, string actionId, string reason)
        {
            if (!IsRealTimeCombatActive || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable)
                return;
            var epoch = Game.RealTimeCombat.Epoch;
            Logger.LogError("Authoritative real-time AI action cannot be applied. Epoch={Epoch}, UnitId={UnitId}, ActionId={ActionId}, Reason={Reason}",
                epoch, unitId, actionId, reason);
            EnterLocalCriticalFaultBarrier(epoch);
            Send(new ClientRealTimeAIActionFault
            {
                Epoch = epoch, UnitId = unitId, ActionId = actionId, Reason = reason
            });
        }

        public bool SubmitRealTimeCombatCommand(RealTimeCommandKind kind, NetworkUnitMoveTo movement = null, NetworkUnitAttack attack = null, NetworkAbilityUse ability = null)
        {
            if (!IsRealTimeCombatActive || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable)
            {
                return false;
            }

            var message = new ClientRealTimeCombatCommand
            {
                Epoch = Game.RealTimeCombat.Epoch,
                PlayerId = Game.LocalPlayerId,
                SessionId = Game.Id,
                LoadedSaveSeed = Game.LoadedSaveSeed,
                ObservedStateHash = Game.RealTimeCombat.CanonicalStateHash,
                Kind = (int)kind,
                Movement = movement == null ? null : Mapper.Map<Networking.Messages.Contracts.NetworkUnitMoveTo>(movement),
                Attack = attack == null ? null : Mapper.Map<Networking.Messages.Contracts.NetworkUnitAttack>(attack),
                Ability = ability == null ? null : Mapper.Map<Networking.Messages.Contracts.NetworkAbilityUse>(ability)
            };
            if (!RealTimeCombatCommandValidator.TryGetUnitId(message, out var unitId)
                || !Game.Characters.Any(x => x.UnitId == unitId && x.Owner?.Id == Game.LocalPlayerId))
            {
                return false;
            }

            lock (_realTimeCommandSequenceLock)
            {
                if (_realTimeCommandEpoch != message.Epoch)
                {
                    _realTimeCommandEpoch = message.Epoch;
                    _nextRealTimeCommandSequence = 0;
                }
                message.Sequence = ++_nextRealTimeCommandSequence;
                message.CommandId = $"{message.PlayerId}:{message.Epoch}:{message.Sequence}";
                Send(message);
            }
            return true;
        }

        public bool RequestRealTimePause(bool wantsPause)
        {
            if (!IsRealTimeCombatActive || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable)
            {
                return false;
            }

            var message = new ClientRealTimePauseRequested
            {
                Epoch = Game.RealTimeCombat.Epoch,
                PlayerId = Game.LocalPlayerId,
                WantsPause = wantsPause
            };
            lock (_realTimeCommandSequenceLock)
            {
                if (_realTimePauseRequestEpoch != message.Epoch)
                {
                    _realTimePauseRequestEpoch = message.Epoch;
                    _nextRealTimePauseRequestSequence = 0;
                }
                message.RequestSequence = ++_nextRealTimePauseRequestSequence;
                Send(message);
            }
            return true;
        }

        public override void OnAreaLoaded()
        {
            base.OnAreaLoaded();

            var message = new ClientAreaLoaded();
            Send(message);
        }

        public void OnAfterCueShow(NetworkDialog networkDialog, string cueName, bool hasSystemAnswer)
        {
            Logger.LogInformation("Showing dialog Cue. DialogId={DialogId}, DialogName={DialogName}, CueName={CueName}, HasSystemAnswer={HasSystemAnswer}", networkDialog.Id, networkDialog.Name, cueName, hasSystemAnswer);
            if (hasSystemAnswer)
            {
                DialogInteraction.SetDialogContinueButtonState(false);
            }

            Game.DialogState.CurrentCueName = cueName;
            Game.DialogState.Answer = null;

            var message = new ClientDialogCueWitnessed
            {
                CueName = cueName,
                Dialog = Mapper.Map<Networking.Messages.Contracts.NetworkDialog>(networkDialog)
            };
            Send(message);
        }

        public bool OnBeforeSelectDialogAnswer(NetworkDialog networkDialog, string cueName, string answerName, bool isExitAnswer, string manualUnitSelectionId)
        {
            if (Game.DialogState == null)
            {
                Logger.LogError("Current dialog is null");
                return false;
            }

            if (Game.DialogState.Answer != null && string.Equals(answerName, Game.DialogState.Answer.AnswerName, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogInformation("Proceeding with dialog answer without extra steps. DialogId={DialogId}, DialogName={DialogName}, CueName={CueName}, AnswerName={AnswerName}", Game.DialogState.Dialog.Id, Game.DialogState.Dialog.Name, cueName, answerName);
                Game.DialogState.IsSelectingAnswer = false;
                return true;
            }

            var message = new ClientDialogCueAnswerSuggested
            {
                Dialog = Mapper.Map<Networking.Messages.Contracts.NetworkDialog>(networkDialog),
                CueName = cueName,
                AnswerName = answerName
            };
            Send(message);

            Game.DialogState.IsSelectingAnswer = true;
            return false;
        }

        public bool StartDialog(NetworkDialog networkDialog)
        {
            if (networkDialog.IsScripted)
            {
                var previousDialog = Game.DialogState;
                Game.DialogState = new NetworkDialogState(networkDialog);
                Logger.LogInformation("Scripted dialog has been started. DialogId={DialogId}, DialogName={Name}", Game.DialogState.Dialog.Id, Game.DialogState.Dialog.Name);
                // workaround for scripted sequential dialogs (e.g. Act4 shamira -> nocticula message dialog)
                if (previousDialog != null)
                {
                    Logger.LogWarning("Previous cue name has been preserved. PreviousDialogName={PreviousDialogName}, NewDialogName={NewDialogName}", previousDialog.Dialog.Name, Game.DialogState.Dialog.Name);
                    Game.DialogState.CurrentCueName = previousDialog.CurrentCueName;
                }

                if (Game.Combat == null)
                {
                    ResetLastCombatTurn();
                }

                ResetManualPause();
                return true;
            }

            if (string.Equals(Game.DialogState?.Dialog.Id, networkDialog.Id, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogInformation("Dialog has been initiated, proceeding with default game logic. Id={Id}, Name={Name}", networkDialog.Id, networkDialog.Name);
                ResetManualPause();
                return true;
            }

            var message = new ClientDialogStartRequested
            {
                Dialog = Mapper.Map<Networking.Messages.Contracts.NetworkDialog>(networkDialog)
            };
            Send(message);

            return false;
        }

        public bool CanInitializeCombat()
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
                    if (!Game.Combat.IsInitiated)
                    {
                        var units = CombatInteraction.GetUnitsInCombat();
                        var message = new NotifyCombatInitiated
                        {
                            PlayerId = Game.LocalPlayerId,
                            InitialUnits = [.. units.Select(x => x.Id)]
                        };
                        Send(message);
                        Game.Combat.IsInitiated = true;
                    }

                    return false;
                case NetworkCombatStage.Collecting:
                    if (!Game.Combat.IsDataCollected)
                    {
                        var unitsInCombat = CombatInteraction.GetUnitsInCombat();
                        var message = new NotifyCombatDataCollected
                        {
                            PlayerId = Game.LocalPlayerId,
                            Units = [.. unitsInCombat.Select(x => x.Id)]
                        };
                        Send(message);
                        Game.Combat.IsDataCollected = true;
                    }

                    return false;
                case NetworkCombatStage.Reconciling:
                case NetworkCombatStage.Resyncing:
                case NetworkCombatStage.Finalizing:
                    return false;
                case NetworkCombatStage.Running:
                    return true;
                default:
                    return false;
            }
        }

        public bool CanContinueCombat()
        {
            if (Game.Combat == null)
            {
                return false;
            }

            var canContinue = Game.Combat.Stage == NetworkCombatStage.Running;
            return canContinue;
        }

        public void OnBeforeTryRollRestRandomEncounter()
        {
            try
            {
                Logger.LogInformation("Retrieving rest random encounter context. SleepPhase={SleepPhase}", Game.Rest.SleepPhase);

                var settings = SettingsService.GetSettings();
                var message = new RandomEncounterContextRequest
                {
                    SleepPhase = Game.Rest.SleepPhase,
                    Timeout = settings.RestEncounterSyncTimeout
                };
                var response = _networkClient.SendAndWaitForAsync<RandomEncounterContextResponse>(message).Result;

                if (response?.Encounter == null)
                {
                    Logger.LogError("Host return null encounter");
                    return;
                }

                var context = new NetworkRandomEncounterContext
                {
                    PreRecorded = Mapper.Map<NetworkRandomEncounter>(response.Encounter)
                };

                Logger.LogInformation("Rest random encounter context has been retrieved. SleepPhase={SleepPhase}, Data={Data}", Game.Rest.SleepPhase, context.PreRecorded);

                GameInteraction.SetRandomEncounterContext(context);

                if (context.PreRecorded.RandomUnitSeed.HasValue)
                {
                    EnsureForcePaused(NetworkForcedPauseReason.RestEncounterLoading);
                    GameInteraction.SetPause(true);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to retrieve rest random encounter context");
                throw;
            }
        }

        public bool OnRequestLevelingUI(string unitId, NetworkLevelingType networkCharGenScreenType)
        {
            if (Game.Leveling != null)
            {
                return true;
            }

            var message = new ClientCharacterLevelingRequested
            {
                UnitId = unitId,
                Type = networkCharGenScreenType.ToString()
            };
            Send(message);
            return false;
        }

        public void OnAutoPausedByTrapDetection()
        {
            if (IsPauseOnCooldown(NetworkForcedPauseReason.TrapDetected))
            {
                // This runs as a postfix, so the game might already be paused at this point
                ForceUnpause();
                return;
            }

            EnsureForcePaused(NetworkForcedPauseReason.TrapDetected, removalDelay: null);
            var message = new ClientGameAutoPaused
            {
                Pause = Mapper.Map<Networking.Messages.Contracts.NetworkForcedPause>(Game.ForcedPause)
            };
            Send(message);
        }

        public bool OnClickGroupChangerUnit(string unitId)
        {
            // client is not allowed to move characters (no restrictions, just to avoid implementing extra synchronization)
            return false;
        }

        public bool OnAreaEffectTriggered(NetworkAreaEffect areaEffect)
        {
            if (Game.Combat == null)
            {
                return true;
            }

            if (Game.Combat.TriggeredAreaEffects.Remove(areaEffect))
            {
                Logger.LogWarning("Area effect trigger has been allowed by host. Id={Id}, Name={Name}", areaEffect.Id, areaEffect.Name);
                return true;
            }

            var canTrigger = Game.Combat.Turn != null;
            return canTrigger;
        }

        public bool OnGlobalMapSelectLocation(NetworkGlobalMapLocation globalMapLocation)
        {
            if (globalMapLocation == null)
            {
                return false;
            }

            var canSelectLocation = GlobalMapInteraction.IsAtLocation(globalMapLocation);
            return canSelectLocation;
        }

        public bool OnSpawnCampPlace(NetworkVector3 position)
        {
            PlayerNotification.ShowWarningNotification(WellKnownKeys.GameNotifications.Rest.NoCampingPermission.Key);
            return false;
        }

        public bool OnCreateAndEquipPolymorphInSlot(NetworkPolymorphicItem polymorphicItem)
        {
            var isInParty = IsControlledByPlayers(polymorphicItem.UnitId) || GameInteraction.IsUnitInParty(polymorphicItem.UnitId);
            if (!isInParty)
            {
                return true;
            }

            var message = new NotifyPolymorphicItemCreationRequested
            {
                PolymorphicItem = Mapper.Map<Networking.Messages.Contracts.NetworkPolymorphicItem>(polymorphicItem)
            };
            Send(message);

            return false;
        }

        public bool OnTacticalCombatInitialization()
        {
            if (Game.ArmyCombat != null && !Game.ArmyCombat.IsInitialized)
            {
                Game.ArmyCombat.IsInitialized = true;
                var message = new NotifyTacticalCombatInitializationConfirmed
                {
                    PlayerId = Game.LocalPlayerId
                };
                Send(message);
                return true;
            }

            ResetPlayersTracker(Game.PlayersInGlobalMapCombatResults);
            Game.ArmyCombat = new NetworkArmyCombat() { IsInitialized = false };

            return false;
        }

        public NetworkAIAction OnAfterAISelectedAction(NetworkAIAction networkAIAction)
        {
            try
            {
                // crusade combat is fine as of now
                if (Game.ArmyCombat != null)
                {
                    return null;
                }

                if (Game.Combat?.Turn == null)
                {
                    return null;
                }

                var action = Game.Combat.Turn.AIActions.FirstOrDefault(a => string.Equals(a.UnitId, networkAIAction.UnitId)
                    && string.Equals(a.Id, networkAIAction.Id, StringComparison.OrdinalIgnoreCase));

                if (action == null && networkAIAction.IsAbility)
                {
                    var firstDifferentAction = Game.Combat.Turn.AIActions
                        .Where(x => !x.IsUsed)
                        .OrderByDescending(x => x.IsAbility)
                        .FirstOrDefault(a => !string.Equals(a.Id, networkAIAction.Id, StringComparison.OrdinalIgnoreCase));

                    // try to use another action (preferably ability)
                    Logger.LogWarning("Requested AI action has not been found within existing actions. UnitId={UnitId}, ActionId={ActionId}, ActionName={ActionName}, ActionType={ActionType}, FallbackActionId={FallbackActionId}, FallbackActionName={FallbackActionName}, FallbackActionType={FallbackActionType}",
                        networkAIAction.UnitId, networkAIAction.Id, networkAIAction.Name, networkAIAction.ActionType, firstDifferentAction?.Id, firstDifferentAction?.Name, firstDifferentAction?.ActionType);

                    action = firstDifferentAction;
                }

                if (action != null)
                {
                    action.IsUsed = true;
                }

                return action;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error while selecting AI action");
                return null;
            }
        }

        protected override bool IsArmyCombatTurnSynced()
        {
            return !Game.ArmyCombat.Turn.IsSyncRequired;
        }

        protected override void Send(object message)
        {
            _networkClient.Broadcast(message);
        }

        protected override void Send(long playerId, object message)
        {
            Send(message);
        }

        protected override void OnLocalPlayerTurnStart()
        {
            var message = new ClientCombatTurnStarted
            {
                UnitId = Game.Combat.Turn.UnitId,
                Round = Game.Combat.Round
            };

            Send(message);
        }

        protected override bool OnToggleOffPause(out bool showReason)
        {
            showReason = false;

            var message = new ClientTogglePauseOff { PlayerId = Game.LocalPlayerId };
            Send(message);
            return false;
        }

        protected override void OnLocalPlayerTurnEnd()
        {
            if (Game.Combat.Turn.AIActions.Count > 0)
            {
                Game.Combat.Turn.AIActions.Clear();
                Logger.LogInformation("AI actions have been cleared");
            }
        }

        protected override void OnSaveGameChunkSaved(int chunkNumber)
        {
            if (Game.StartUp.AutoStart)
            {
                return;
            }

            var confirmation = new NotifySaveGameChunkReceived { ChunkNumber = chunkNumber };
            Send(confirmation);
        }

        protected override void OnSaveGameChunksTransferCompleted()
        {
            var confirmation = new NotifySaveGameSyncStatusChanged { PlayerId = Game.LocalPlayerId, Status = NetworkLobbySyncStatus.Succeed.ToString() };
            Send(confirmation);
        }

        protected override void SetupNetworkMessageHandlers()
        {
            _networkClient.OnError = OnNetworkClientError;
            _networkClient.OnConnected = OnNetworkClientConnected;

            base.SetupNetworkMessageHandlers();

            _networkClient
               // lobby
               .On<GameServerConnectionSucceeded>(OnGameServerConnectionSucceeded)
               .On<NotifyLobbyPlayersChanged>(OnNotifyLobbyPlayersChanged)
               .On<NotifyLobbyCharactersChanged>(OnNotifyLobbyCharactersChanged)
               .On<NotifyCharacterOwnerChanged>(OnNotifyCharacterOwnerChanged)
               .On<NotifyGameStarted>(OnNotifyGameStarted)
               .On<NotifySaveGameSyncStatusChanged>(OnNotifyLobbySyncStatusChanged)
               .On<NotifySaveGameTransferProgressChanged>(OnNotifySaveGameTransferProgressChanged)
               .On<NotifyNewGameDifficultyChanged>(OnNotifyNewGameDifficultyChanged)

               // new game sequence
               .On<NotifyNewGameSequencePhaseChanged>(OnNotifyNewGameSequencePhaseChanged)
               .On<NotifyNewGameSequenceLevelingStarted>(OnNotifyNewGameSequenceLevelingStarted)
               .On<NotifyNewGameSequenceTerminated>(OnNotifyNewGameSequenceTerminated)
               .On<NotifyNewGameSequenceCampaignChanged>(OnNotifyNewGameSequenceCampaignChanged)
               .On<NotifyNewGameSequenceLastAzlantiChanged>(OnNotifyNewGameSequenceLastAzlantiChanged)

               // pausing
               .On<NotifyGamePauseEnded>(OnNotifyGamePauseEnded)

               // area transitioning
               .On<NotifyPartyAreaTransitioned>(OnNotifyPartyAreaTransitioned)
               .On<NotifyTransitionMapEntryChosen>(OnNotifyTransitionMapEntryChosen)
               .On<NotifyTransitionMapClosed>(OnNotifyTransitionMapClosed)
               .On<NotifyIslandMapEntryChosen>(OnNotifyIslandMapEntryChosen)
               .On<NotifyAreaLoadingCompleted>(OnNotifyAreaLoadingCompleted)

               // leveling
               .On<NotifyLevelingStarted>(OnNotifyLevelingStarted)

               // character selection window
               .On<NotifyCharacterSelectionToggleChanged>(OnNotifyCharacterSelectionToggleChanged)
               .On<NotifyCharacterSelectionWindowAccepted>(OnNotifyCharacterSelectionWindowAccepted)
               .On<NotifyCharacterSelectionWindowClosed>(OnNotifyCharacterSelectionWindowClosed)

               // rest
               .On<NotifyRestWindowClosed>(OnNotifyRestWindowClosed)
               .On<NotifyRestStarted>(OnNotifyRestStarted)
               .On<NotifySpawnCampPlace>(OnNotifySpawnCampPlace)
               .On<NotifyCampingUseHealingSpellsChanged>(OnNotifyCampingUseHealingSpellsChanged)
               .On<NotifyCampingStateChanged>(OnNotifyCampingStateChanged)
               .On<NotifyCampingUnitsRoleChanged>(OnNotifyCampingUnitsRoleChanged)

               // combat
               .On<NotifyCombatInitiated>(OnNotifyCombatInitiated)
               .On<NotifyCombatStageChanged>(OnNotifyCombatStageChanged)
               .On<NotifyCombatDataDiscrepancyDetected>(OnNotifyCombatDataDiscrepancyDetected)
               .On<NotifyCombatUnitsExcluded>(OnNotifyCombatUnitsExcluded)
               .On<NotifyCombatInitializationRequired>(OnNotifyCombatInitializationRequired)
               .On<NotifyInvalidCombatTurnStarted>(OnNotifyInvalidCombatTurnStarted)
               .On<NotifyCombatTurnStartSynchronizationRequired>(OnNotifyCombatTurnStartSynchronizationRequired)
               .On<NotifyCombatTurnEndSynchronizationRequired>(OnNotifyCombatTurnEndSynchronizationRequired)
               .On<NotifyCombatTurnStarted>(OnNotifyCombatTurnStarted)
               .On<NotifyAIActionSelected>(OnNotifyAIActionExecuted)
               .On<NotifyCombatLocalTurnEnded>(OnNotifyCombatLocalTurnEnded)
               .On<NotifyCombatTurnEnded>(OnNotifyCombatTurnEnded)
               .On<NotifyRealTimeCombatSnapshot>(OnNotifyRealTimeCombatSnapshot)
               .On<NotifyRealTimeCombatCommandAccepted>(OnNotifyRealTimeCombatCommandAccepted)
               .On<NotifyRealTimeCombatCommandRejected>(OnNotifyRealTimeCombatCommandRejected)
               .On<NotifyRealTimeAuthoritativeEvent>(OnNotifyRealTimeAuthoritativeEvent)
               .On<NotifyRealTimePauseChanged>(OnNotifyRealTimePauseChanged)
               .On<NotifyRealTimeModeTransition>(OnNotifyRealTimeModeTransition)
               .On<NotifyRealTimeRepair>(OnNotifyRealTimeRepair)
               .On<NotifyRealTimeCombatPreference>(OnNotifyRealTimeCombatPreference)
               .On<NotifyRealTimeRuleRoll>(OnNotifyRealTimeRuleRoll)
               .On<NotifyRealTimeEntityId>(OnNotifyRealTimeEntityId)
               .On<NotifyRealTimeUnitKilled>(OnNotifyRealTimeUnitKilled)

               // global map & crusade combat
               .On<NotifyGlobalMapRestOpened>(OnNotifyGlobalMapRestOpened)
               .On<NotifyGlobalMapGroupChangerOpened>(OnNotifyGlobalMapGroupChangerOpened)
               .On<NotifyGlobalMapTravelStarted>(OnNotifyGlobalMapTravelStarted)
               .On<NotifyGlobalMapTravelStopped>(OnNotifyGlobalMapTravelStopped)
               .On<NotifyGlobalMapTravelContinued>(OnNotifyGlobalMapTravelContinued)
               .On<NotifyGlobalMapCommonPopupAccepted>(OnNotifyGlobalMapIngredientCollectionAccepted)
               .On<NotifyGlobalMapEncounterAccepted>(OnNotifyGlobalMapEncounterAccepted)
               .On<NotifyGlobalMapEncounterAvoided>(OnNotifyGlobalMapEncounterAvoided)
               .On<NotifyGlobalMapEncounterRolled>(OnNotifyGlobalMapEncounterRolled)
               .On<NotifyGlobalMapLocationMessageClosed>(OnNotifyGlobalMapLocationMessageClosed)
               .On<NotifyGlobalMapLocationMessageAccepted>(OnNotifyGlobalMapLocationMessageAccepted)
               .On<NotifyGlobalMapCommonPopupDeclined>(OnNotifyGlobalMapCommonPopupDeclined)
               .On<NotifyGlobalMapDaySkipped>(OnNotifyGlobalMapDaySkipped)
               .On<NotifyGlobalMapSelectedArmyChanged>(OnNotifyGlobalMapSelectedArmyChanged)
               .On<NotifyGlobalMapAutoCrusadeCombatChanged>(OnNotifyGlobalMapAutoCrusadeCombatChanged)
               .On<NotifyGlobalMapCombatResultsClosed>(OnNotifyGlobalMapCombatResultsClosed)
               .On<NotifyCrusadeArmyBattleResultsManualCombatStarted>(OnNotifyCrusadeArmyBattleResultsManualCombatStarted)
               .On<NotifyCrusadeArmyBattleResultsClosed>(OnNotifyCrusadeArmyBattleResultsClosed)
               .On<NotifyTacticalCombatInitialized>(OnNotifyTacticalCombatInitialized)
               .On<NotifyTacticalUnitAttackCommandExecuted>(OnNotifyTacticalUnitAttackCommandExecuted)
               .On<NotifyTacticalUnitUseAbilityCommandExecuted>(OnNotifyTacticalUnitUseAbilityCommandExecuted)
               .On<NotifyTacticalUnitMoveToCommandExecuted>(OnNotifyTacticalUnitMoveToCommandExecuted)
               .On<NotifyTacticalCombatTurnPostponed>(OnNotifyTacticalCombatTurnPostponed)
               .On<NotifyTacticalCombatTotalDefenseUsed>(OnNotifyTacticalCombatTotalDefenseUsed)
               .On<NotifyTacticalCombatRetreated>(OnNotifyTacticalCombatRetreated)
               .On<NotifyTacticalCombatAccelerationChanged>(OnNotifyTacticalCombatAccelerationChanged)
               .On<NotifyGlobalMapCrusadeArmySquadSplit>(OnNotifyGlobalMapCrusadeArmySquadSplit)
               .On<NotifyGlobalMapCrusadeArmySquadsMerged>(OnNotifyGlobalMapCrusadeArmySquadsMerged)
               .On<NotifyGlobalMapCrusadeArmySquadsSwitched>(OnNotifyGlobalMapCrusadeArmySquadsSwitched)
               .On<NotifyGlobalMapCrusadeArmySquadSplitRequested>(OnNotifyGlobalMapCrusadeArmySquadSplitRequested)
               .On<NotifyGlobalMapCrusadeArmyMergedInOne>(OnNotifyGlobalMapCrusadeArmyMergedInOne)
               .On<NotifyGlobalMapCrusadeArmySquadDismissed>(OnNotifyGlobalMapCrusadeArmySquadDismissed)
               .On<NotifyGlobalMapCrusadeArmyInfoClosed>(OnNotifyGlobalMapCrusadeArmyInfoClosed)
               .On<NotifyGlobalMapCrusadeArmyInfoShown>(OnNotifyGlobalMapCrusadeArmyInfoShown)
               .On<NotifyGlobalMapCrusadeArmySquadsMovedToMainArmy>(OnNotifyGlobalMapCrusadeArmySquadsMovedToMainArmy)
               .On<NotifyGlobalMapCrusadeArmySquadsMovedToSecondArmy>(OnNotifyGlobalMapCrusadeArmySquadsMovedToSecondArmy)
               .On<NotifyGlobalMapCrusadeArmyInfoNextMergeArmySelected>(OnNotifyGlobalMapCrusadeArmyInfoNextMergeArmySelected)
               .On<NotifyGlobalMapCrusadeArmyInfoPrevMergeArmySelected>(OnNotifyGlobalMapCrusadeArmyInfoPrevMergeArmySelected)
               .On<NotifyGlobalMapCrusadeArmyLeaderActionExecuted>(OnNotifyGlobalMapCrusadeArmyLeaderActionExecuted)
               .On<NotifyGlobalMapCrusadeArmiesMerging>(OnNotifyGlobalMapCrusadeArmiesMerging)
               .On<NotifyGlobalMapCrusadeArmyCreated>(OnNotifyGlobalMapCrusadeArmyCreated)
               .On<NotifyGlobalMapCrusadeArmyMainCartClosed>(OnNotifyGlobalMapCrusadeArmyMainCartClosed)
               .On<NotifyGlobalMapCrusadeArmyMergeCartClosed>(OnNotifyGlobalMapCrusadeArmyMergeCartClosed)
               .On<NotifyGlobalMapCrusadeArmyInfoCartNameChanged>(OnNotifyGlobalMapCrusadeArmyInfoCartNameChanged)
               .On<NotifyGlobalMapCrusadeArmySetLeaderClosed>(OnNotifyGlobalMapCrusadeArmySetLeaderClosed)
               .On<NotifyGlobalMapCrusadeArmySetLeaderClearClicked>(OnNotifyGlobalMapCrusadeArmySetLeaderClearClicked)
               .On<NotifyGlobalMapCrusadeArmySetLeaderRecruitClicked>(OnNotifyGlobalMapCrusadeArmySetLeaderRecruitClicked)
               .On<NotifyGlobalMapCrusadeArmyBuyLeaderClosed>(OnNotifyGlobalMapCrusadeArmyBuyLeaderClosed)
               .On<NotifyGlobalMapRecruitmentMercenariesRerolled>(OnNotifyGlobalMapRecruitmentMercenariesRerolled)
               .On<NotifyGlobalMapRecruitmentNextArmySelected>(OnNotifyGlobalMapRecruitmentNextArmySelected)
               .On<NotifyGlobalMapRecruitmentPrevArmySelected>(OnNotifyGlobalMapRecruitmentPrevArmySelected)
               .On<NotifyGlobalMapUnitsRecruited>(OnNotifyGlobalMapUnitsRecruited)
               .On<NotifyGlobalMapResourcesBought>(OnNotifyGlobalMapResourcesBought)
               .On<NotifyGlobalMapRecruitmentShown>(OnNotifyGlobalMapRecruitmentShown)
               .On<NotifyGlobalMapRecruitmentClosed>(OnNotifyGlobalMapRecruitmentClosed)
               .On<NotifyGlobalMapCrusadeArmyDismissed>(OnNotifyGlobalMapCrusadeArmyDismissed)
               .On<NotifyGlobalMapCrusadeArmyRecruitCartClosed>(OnNotifyGlobalMapCrusadeArmyRecruitCartClosed)
               .On<NotifyGlobalMapMagicSpellUsed>(OnNotifyGlobalMapMagicSpellUsed)
               .On<NotifyGlobalMapCrusadeArmyLeaderLevelingClosed>(OnNotifyGlobalMapCrusadeArmyLeaderLevelingClosed)
               .On<NotifyGlobalMapCrusadeArmyLeaderLevelingConfirmed>(OnNotifyGlobalMapCrusadeArmyLeaderLevelingConfirmed)
               .On<NotifyGlobalMapCrusadeArmyLeaderLevelingSkillSelected>(OnNotifyGlobalMapCrusadeArmyLeaderLevelingSkillSelected)
               .On<NotifyGlobalMapCommonPopupShown>(OnNotifyGlobalMapCommonPopupShown)
               .On<NotifyGlobalMapTeleport>(OnNotifyGlobalMapTeleport)
               .On<NotifyArmyCombatTurnSynchronizationRequired>(OnNotifyArmyCombatTurnSynchronizationRequired)

               // kingdom
               .On<NotifyKingdomNavigationChanged>(OnNotifyKingdomNavigationChanged)
               .On<NotifyKingdomEventSelected>(OnNotifyKingdomEventSelected)
               .On<NotifyKingdomEventSolutionSelected>(OnNotifyKingdomEventSolutionSelected)
               .On<NotifyKingdomEventStarted>(OnNotifyKingdomEventStarted)
               .On<NotifyKingdomEventCancelled>(OnNotifyKingdomEventCancelled)
               .On<NotifyKingdomEventDropped>(OnNotifyKingdomEventDropped)
               .On<NotifyKingdomSettlementEntered>(OnNotifyKingdomSettlementEntered)
               .On<NotifyKingdomSettlementLeft>(OnNotifyKingdomSettlementLeft)
               .On<NotifyKingdomSettlementBuildingSold>(OnNotifyKingdomSettlementBuildingSold)
               .On<NotifyKingdomSettlementBuildingBuilt>(OnNotifyKingdomSettlementBuildingBuilt)
               .On<NotifyKingdomSettlementUpgraded>(OnNotifyKingdomSettlementUpgraded)

               // dialogs
               .On<NotifyDialogStarted>(OnNotifyDialogStarted)
               .On<NotifyDialogCueAnswerSuggested>(OnNotifyDialogCueAnswerSuggested)
               .On<NotifyDialogCueAnswerSelected>(OnNotifyDialogCueAnswerSelected)
               .On<NotifyDialogPopupClosed>(OnNotifyDialogPopupClosed)
               .On<NotifyDialogPopupAccepted>(OnNotifyDialogPopupAccepted)

               // vendor interaction
               .On<NotifyVendorDealMade>(OnNotifyVendorDealMade)
               .On<NotifyVendorWindowClosed>(OnNotifyVendorWindowClosed)

               // inspection
               .On<NotifyPerceptionCheckRolled>(OnNotifyPerceptionCheckRolled)
               .On<NotifyInspectionKnowledgeCheckRolled>(OnNotifyInspectionKnowledgeCheckRolled)
               .On<NotifyStealthPerceptionCheckRolled>(OnNotifyStealthPerceptionCheckRolled)

               // group management
               .On<NotifyGroupChangerClosed>(OnNotifyGroupChangerClosed)
               .On<NotifyGroupChangerUnitClicked>(OnNotifyGroupChangerUnitClicked)
               .On<NotifyGroupChangerPartyAccepted>(OnNotifyGroupChangerPartyAccepted)

               // skip time
               .On<NotifySkipTimeClosed>(OnNotifySkipTimeClosed)
               .On<NotifySkipTimeHoursChanged>(OnNotifySkipTimeHoursChanged)
               .On<NotifySkipTimeStarted>(OnNotifySkipTimeStarted)

               // zone loot
               .On<NotifyZoneLootCompleted>(OnNotifyZoneLootCompleted)
               .On<NotifyZoneLootLeft>(OnNotifyZoneLootLeft)
               .On<NotifyZoneLootRemoveToggleChanged>(OnNotifyZoneLootRemoveToggleChanged)

               // inventory
               .On<NotifyPolymorphicItemCreated>(OnNotifyPolymorphicItemCreated)

               // alushenyrra
               .On<NotifyAlyshenyrraCameraDirectionChanged>(OnNotifyAlyshenyrraCameraDirectionChanged)

               // dungeon
               .On<NotifyDungeonGameOverNewGameStarted>(OnNotifyDungeonGameOverNewGameStarted)
               .On<NotifyDungeonGameOverLastSaveLoaded>(OnNotifyDungeonGameOverLastSaveLoaded)
               .On<NotifyDungeonGameOverTerminated>(OnNotifyDungeonGameOverTerminated)
               .On<NotifyDungeonBoonSelected>(OnNotifyDungeonBoonSelected)
               .On<NotifyDungeonBoonConfirmed>(OnNotifyDungeonBoonConfirmed)
               ;
        }

        private async void OnNotifyArmyCombatTurnSynchronizationRequired(long receivedFrom, NotifyArmyCombatTurnSynchronizationRequired message)
        {
            var units = Mapper.Map<List<NetworkUnit>>(message.Units);

            await CombatInteraction.UpdateArmyCombatUnitsAsync(units);

            Game.ArmyCombat.Turn.IsSyncRequired = false;
            var confirmation = new ClientArmyCombatTurnSynchronized();
            Send(confirmation);
        }

        private void OnNotifyDungeonBoonConfirmed(long receivedFrom, NotifyDungeonBoonConfirmed message)
        {
            ResetPlayersTracker(Game.PlayersInDungeonBoonSelector);
            GameInteraction.ConfirmDungeonBoon();
        }

        private void OnNotifyDungeonBoonSelected(long receivedFrom, NotifyDungeonBoonSelected message)
        {
            var boon = Mapper.Map<NetworkBoon>(message.Boon);
            GameInteraction.SelectDungeonBoon(boon);
        }

        private void OnNotifyDungeonGameOverNewGameStarted(long receivedFrom, NotifyDungeonGameOverNewGameStarted message)
        {
            GameInteraction.StartNewDungeonGame();
        }

        private void OnNotifyDungeonGameOverLastSaveLoaded(long receivedFrom, NotifyDungeonGameOverLastSaveLoaded message)
        {
            GameInteraction.LoadLatestDungeonSaveGame();
        }

        private void OnNotifyDungeonGameOverTerminated(long receivedFrom, NotifyDungeonGameOverTerminated message)
        {
            GameInteraction.ExitDungeonToMainMenu();
        }

        private void OnNotifyNewGameSequenceLastAzlantiChanged(long receivedFrom, NotifyNewGameSequenceLastAzlantiChanged message)
        {
            GameInteraction.SetNewGameLastAzlanti(message.IsEnabled);
        }

        private void OnNotifyNewGameSequenceCampaignChanged(long receivedFrom, NotifyNewGameSequenceCampaignChanged message)
        {
            var campaign = Mapper.Map<NetworkCampaign>(message.Campaign);
            GameInteraction.SetNewGameCampaign(campaign);
        }

        private async void OnNotifyCombatInitiated(long receivedFrom, NotifyCombatInitiated message)
        {
            SetPauseCooldown();

            var isInCombat = await WaitWhileTrue(() => Game.Combat == null, "Waiting for combat to be initiated", TimeSpan.FromSeconds(30));
            if (!isInCombat)
            {
                // host will be locked in combat startup sequence forever until every other client has entered combat locally
                Logger.LogError("Local combat has never been started");
                return;
            }

            // host combat has been restarted for some reason (game does it sometimes seemingly for no reason)
            // all we can do is to restart it locally as well
            if (Game.Combat.Stage != NetworkCombatStage.Initiating)
            {
                Logger.LogWarning("Host combat has been restarted for some reason, but client already advanced further in startup sequence");
                var player = GetHost();
                PlayerNotification.AddCombatText(WellKnownKeys.GameNotifications.Combat.Start.DesyncedStartup.Host.Key, CombatTextSeverity.Critical, player?.Name);

                await CombatInteraction.ForceResetCombatAsync();
                return;
            }
        }

        private async void OnNotifyCombatUnitsExcluded(long receivedFrom, NotifyCombatUnitsExcluded message)
        {
            await CombatInteraction.ExcludeUnitsFromCombatAsync(message.Units);
            var confirmation = new NotifyCombatUnitsExclusionConfirmed();
            Send(confirmation);
        }

        private async void OnNotifyCombatDataDiscrepancyDetected(long receivedFrom, NotifyCombatDataDiscrepancyDetected message)
        {
            var unavailableUnits = await CombatInteraction.EnsureUnitsInCombatAsync(message.Units);
            var confirmation = new NotifyCombatDataDiscrepancyConfirmed
            {
                UnavailableUnits = unavailableUnits
            };
            Send(confirmation);
        }

        private void OnNotifyCombatStageChanged(long receivedFrom, NotifyCombatStageChanged message)
        {
            if (Game.Combat == null)
            {
                Logger.LogError("Combat is not started locally");
                return;
            }

            var stage = Mapper.Map<NetworkCombatStage>(message.Stage);
            SetCombatStage(stage, isSilent: true);
        }

        private void OnNotifySaveGameTransferProgressChanged(long receivedFrom, NotifySaveGameTransferProgressChanged message)
        {
            OnSaveGameTransferProgressChanged?.Invoke(message.Progress);
        }

        private void OnNotifyAlyshenyrraCameraDirectionChanged(long receivedFrom, NotifyAlyshenyrraCameraDirectionChanged message)
        {
            GameInteraction.ChangeAlushenyrraIslesState(message.Direction);
        }

        private void OnNotifyAreaLoadingCompleted(long receivedFrom, NotifyAreaLoadingCompleted message)
        {
            SetAreaSeed(message.AreaSeed);
        }

        private void OnNotifyKingdomSettlementUpgraded(long receivedFrom, NotifyKingdomSettlementUpgraded message)
        {
            var kingdomSettlement = Mapper.Map<NetworkKingdomSettlement>(message.Settlement);
            GlobalMapInteraction.UpgradeSettlement(kingdomSettlement);
        }

        private void OnNotifyIslandMapEntryChosen(long receivedFrom, NotifyIslandMapEntryChosen message)
        {
            var island = Mapper.Map<NetworkIslandMapTransition>(message.Island);
            GameInteraction.ChooseIslandMapEntry(island);
        }

        private void OnNotifyGlobalMapTeleport(long receivedFrom, NotifyGlobalMapTeleport message)
        {
            var location = Mapper.Map<NetworkGlobalMapLocation>(message.Location);
            GlobalMapInteraction.Teleport(location);
        }

        private void OnNotifyTransitionMapEntryChosen(long receivedFrom, NotifyTransitionMapEntryChosen message)
        {
            ResetPlayersTracker(Game.PlayersInTransitionMap);
            GameInteraction.ChooseTransitionMapEntry(message.EntryId);
        }

        private void OnNotifyTransitionMapClosed(long receivedFrom, NotifyTransitionMapClosed message)
        {
            ResetPlayersTracker(Game.PlayersInTransitionMap);
            GameInteraction.CloseTransitionMap();
        }

        private void OnNotifyKingdomSettlementBuildingBuilt(long receivedFrom, NotifyKingdomSettlementBuildingBuilt message)
        {
            var building = Mapper.Map<NetworkKingdomSettlementBuilding>(message.Building);
            GlobalMapInteraction.BuildSettlementBuilding(building);
        }

        private void OnNotifyKingdomSettlementBuildingSold(long receivedFrom, NotifyKingdomSettlementBuildingSold message)
        {
            var building = Mapper.Map<NetworkKingdomSettlementBuilding>(message.Building);
            GlobalMapInteraction.SellSettlementBuilding(building);
        }

        private void OnNotifyKingdomSettlementLeft(long receivedFrom, NotifyKingdomSettlementLeft message)
        {
            GlobalMapInteraction.LeaveSettlement();
        }

        private void OnNotifyKingdomSettlementEntered(long receivedFrom, NotifyKingdomSettlementEntered message)
        {
            var settlement = Mapper.Map<NetworkKingdomSettlement>(message.Settlement);
            GlobalMapInteraction.EnterSettlement(settlement, message.RequiresUnloadEvent, message.ExitSettlementToGlobalMap);
        }

        private void OnNotifyKingdomEventDropped(long receivedFrom, NotifyKingdomEventDropped message)
        {
            var kingdomEvent = Mapper.Map<NetworkKingdomEvent>(message.Event);
            GlobalMapInteraction.DropKingdomEvent(kingdomEvent);
        }

        private void OnNotifyKingdomEventCancelled(long receivedFrom, NotifyKingdomEventCancelled message)
        {
            GlobalMapInteraction.CancelKingdomEvent();
        }

        private void OnNotifyKingdomEventStarted(long receivedFrom, NotifyKingdomEventStarted message)
        {
            GlobalMapInteraction.StartKingdomEvent();
        }

        private void OnNotifyKingdomEventSolutionSelected(long receivedFrom, NotifyKingdomEventSolutionSelected message)
        {
            var solution = Mapper.Map<NetworkKingdomEventSolution>(message.Solution);
            GlobalMapInteraction.SelectKingdomEventSolution(solution);
        }

        private void OnNotifyKingdomEventSelected(long receivedFrom, NotifyKingdomEventSelected message)
        {
            var kingdomEvent = Mapper.Map<NetworkKingdomEvent>(message.Event);
            GlobalMapInteraction.SelectKingdomEvent(kingdomEvent);
        }

        private void OnNotifyKingdomNavigationChanged(long receivedFrom, NotifyKingdomNavigationChanged message)
        {
            var navigation = Mapper.Map<KingdomNavigationType>(message.Type);
            GlobalMapInteraction.ChangeKingdomNavigation(navigation);
        }

        private async void OnNotifyCombatTurnEndSynchronizationRequired(long receivedFrom, NotifyCombatTurnEndSynchronizationRequired message)
        {
            if (receivedFrom != NetworkConstants.HostPlayerId || message == null
                || message.SynchronizationSequence <= 0)
                return;
            var session = Game;
            var turn = session?.Combat?.Turn;
            await _turnEndSynchronizationGate.WaitAsync();
            try
            {
                if (turn == null || session.Combat?.Stage != NetworkCombatStage.Running)
                    return;
                lock (ActionLock)
                {
                    if (!ReferenceEquals(_turnEndSynchronizationSession, session))
                    {
                        _turnEndSynchronizationSession = session;
                        _lastTurnEndSynchronizationSequence = 0;
                    }
                    if (message.SynchronizationSequence < _lastTurnEndSynchronizationSequence)
                        return;
                    _lastTurnEndSynchronizationSequence = message.SynchronizationSequence;
                }
                if (!string.Equals(turn.UnitId, message.UnitId, StringComparison.OrdinalIgnoreCase))
                {
                    _turnEndSynchronizationFailed = true;
                    Logger.LogError("Turn-end synchronization targets a different unit. HostUnitId={HostUnitId}, ClientUnitId={ClientUnitId}, Sequence={Sequence}",
                        message.UnitId, turn.UnitId, message.SynchronizationSequence);
                    Send(new ClientCombatTurnEndSynchronized
                    {
                        UnitId = message.UnitId, PlayerId = session.LocalPlayerId,
                        SynchronizationSequence = message.SynchronizationSequence,
                        Succeeded = false, FailureReason = "wrong-current-unit"
                    });
                    return;
                }
                var finished = await WaitWhileTrue(() => ReferenceEquals(Game, session)
                    && ReferenceEquals(session.Combat?.Turn, turn)
                    && _lastTurnEndSynchronizationSequence == message.SynchronizationSequence
                    && !CombatInteraction.IsCombatTurnFinished(),
                    "Waiting for an active turn to end before proceeding with synchronization");
                if (!ReferenceEquals(Game, session) || !ReferenceEquals(session.Combat?.Turn, turn)
                    || _lastTurnEndSynchronizationSequence != message.SynchronizationSequence)
                    return;

                var isSynced = false;
                var failureReason = finished ? "unit-checkpoint-application-failed" : "turn-still-active";
                if (finished)
                {
                    try
                    {
                        var units = Mapper.Map<List<NetworkUnit>>(message.Units);
                        isSynced = await CombatInteraction.UpdateUnitsAsync(units, updatePosition: true);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Turn-end unit synchronization threw. UnitId={UnitId}, Sequence={Sequence}",
                            message.UnitId, message.SynchronizationSequence);
                    }
                }
                if (!ReferenceEquals(Game, session) || !ReferenceEquals(session.Combat?.Turn, turn)
                    || _lastTurnEndSynchronizationSequence != message.SynchronizationSequence)
                    return;
                _turnEndSynchronizationFailed = !isSynced;
                if (!isSynced)
                    Logger.LogError("Failed to synchronize turn end; requesting host retry. UnitId={UnitId}, Sequence={Sequence}, Reason={Reason}",
                        message.UnitId, message.SynchronizationSequence, failureReason);
                Send(new ClientCombatTurnEndSynchronized
                {
                    UnitId = message.UnitId, PlayerId = session.LocalPlayerId,
                    SynchronizationSequence = message.SynchronizationSequence,
                    Succeeded = isSynced,
                    FailureReason = isSynced ? null : failureReason
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Turn-end synchronization failed unexpectedly. UnitId={UnitId}, Sequence={Sequence}",
                    message.UnitId, message.SynchronizationSequence);
                if (ReferenceEquals(Game, session) && ReferenceEquals(session?.Combat?.Turn, turn)
                    && turn != null
                    && _lastTurnEndSynchronizationSequence == message.SynchronizationSequence)
                {
                    _turnEndSynchronizationFailed = true;
                    Send(new ClientCombatTurnEndSynchronized
                    {
                        UnitId = message.UnitId, PlayerId = session.LocalPlayerId,
                        SynchronizationSequence = message.SynchronizationSequence,
                        Succeeded = false, FailureReason = "unexpected-sync-error"
                    });
                }
            }
            finally
            {
                _turnEndSynchronizationGate.Release();
            }
        }

        private void OnNotifyCombatTurnEnded(long receivedFrom, NotifyCombatTurnEnded message)
        {
            if (receivedFrom != NetworkConstants.HostPlayerId || message == null
                || !ReferenceEquals(_turnEndSynchronizationSession, Game)
                || message.SynchronizationSequence != _lastTurnEndSynchronizationSequence
                || !string.Equals(Game?.Combat?.Turn?.UnitId, message.UnitId,
                    StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning("Ignoring stale turn-end completion. UnitId={UnitId}, Sequence={Sequence}, CurrentUnitId={CurrentUnitId}, CurrentSequence={CurrentSequence}",
                    message?.UnitId, message?.SynchronizationSequence, Game?.Combat?.Turn?.UnitId,
                    _lastTurnEndSynchronizationSequence);
                return;
            }
            _turnEndSynchronizationFailed = false;
            SetCombatTurnStage(NetworkCombatTurnStage.Ended);
        }

        private async void OnNotifyRealTimeCombatSnapshot(long receivedFrom, NotifyRealTimeCombatSnapshot message)
        {
            if (receivedFrom != NetworkConstants.HostPlayerId || message == null) return;
            await _realTimeSnapshotGate.WaitAsync();
            NetworkGame session = null;
            NetworkRealTimeCombatState combat = null;
            try
            {
                if (Game?.RealTimeCombat?.Mode != CombatMode.RealTimeWithPause || message.State == null
                    || !string.Equals(message.SessionId, Game.Id, StringComparison.Ordinal)
                    || message.LoadedSaveSeed != Game.LoadedSaveSeed)
                {
                    return;
                }

                session = Game;
                combat = session.RealTimeCombat;
                var coordinator = new RealTimeCombatCoordinator(combat);
                if (!coordinator.IsCurrent(message.Epoch, message.Sequence))
                {
                    Logger.LogDebug("Ignoring stale real-time combat snapshot. Epoch={Epoch}, Sequence={Sequence}", message.Epoch, message.Sequence);
                    return;
                }

                combat.ResponseTimeMs = Math.Max(0, message.ResponseTimeMs);
                combat.MissingSnapshotReplies = Math.Max(0, message.MissingReplies);

                var state = Mapper.Map<NetworkCombatState>(message.State);
                // Apply and sample inside one main-thread dispatch. A game tick between two
                // separate dispatches makes a moving unit appear divergent even after correction.
                var actual = await CombatInteraction.TryApplyAndReadRealTimeCombatStateAsync(state,
                    () => IsCurrentRealTimeMessage(session, combat, message.Epoch)
                        && coordinator.IsCurrent(message.Epoch, message.Sequence));
                if (actual == null || !IsCurrentRealTimeMessage(session, combat, message.Epoch)
                    || !coordinator.IsCurrent(message.Epoch, message.Sequence))
                    return;
                if (RealTimePresentationRuntime.Enabled)
                {
                    if (!ReferenceEquals(_presentationSession, session)
                        || !ReferenceEquals(_presentationCombat, combat)
                        || _presentationEpoch != combat.Epoch
                        || _presentationSaveSeed != session.LoadedSaveSeed)
                    {
                        _presentationSession = session;
                        _presentationCombat = combat;
                        _presentationEpoch = combat.Epoch;
                        _presentationSaveSeed = session.LoadedSaveSeed;
                        _presentationInbox = new RealTimePresentationInbox();
                    }
                    if (message.IncludedEventSequence > _presentationInbox.AppliedSequence)
                    {
                        CombatInteraction.TickRealTimePresentation(false);
                        _presentationInbox.RestoreBaseline(message.IncludedEventSequence);
                    }
                }
                var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot
                {
                    Combat = actual,
                    ActiveOrders = RealTimeAcceptedOrderLedger.Snapshot(combat).ToList()
                });
                var temporalMismatch = RealTimeCombatTemporalDiff.Summarize(state, actual);
                combat.LastAuthoritativeSequence = message.Sequence;
                combat.CanonicalStateHash = message.CanonicalStateHash;
                Send(new ClientRealTimeStateHash
                {
                    Epoch = message.Epoch,
                    Sequence = message.Sequence,
                    CanonicalStateHash = hash,
                    Applied = temporalMismatch == null,
                    FailureReason = temporalMismatch ??
                        (hash == message.CanonicalStateHash ? null : RealTimeCombatDiff.Summarize(state, actual))
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Real-time combat snapshot could not be applied. Epoch={Epoch}, Sequence={Sequence}", message.Epoch, message.Sequence);
                if (session == null || combat == null
                    || !IsCurrentRealTimeMessage(session, combat, message.Epoch))
                    return;
                var reason = $"{ex.GetType().Name}:{ex.Message}";
                Send(new ClientRealTimeStateHash
                {
                    Epoch = message.Epoch,
                    Sequence = message.Sequence,
                    Applied = false,
                    FailureReason = reason.Length > 512 ? reason.Substring(0, 512) : reason
                });
            }
            finally
            {
                _realTimeSnapshotGate.Release();
            }
        }

        private void OnNotifyRealTimeRuleRoll(long receivedFrom, NotifyRealTimeRuleRoll message)
        {
            if (message == null || receivedFrom != NetworkConstants.HostPlayerId
                || !IsRealTimeCombatActive || Game.RealTimeCombat.Epoch != message.Epoch)
                return;
            if (!_realTimeRuleRolls.Receive(message.Epoch, message.Key, message.Sequence, message.Result))
                Logger.LogWarning("Ignoring stale, duplicate or overflowing host combat roll. Epoch={Epoch}, Sequence={Sequence}, Key={Key}",
                    message.Epoch, message.Sequence, message.Key);
        }

        private void OnNotifyRealTimeEntityId(long receivedFrom, NotifyRealTimeEntityId message)
        {
            if (message == null || receivedFrom != NetworkConstants.HostPlayerId
                || !IsRealTimeCombatActive || Game.RealTimeCombat.Epoch != message.Epoch)
                return;
            if (!_realTimeEntityIds.Receive(message.Epoch, message.Key, message.Sequence, message.EntityId))
                Logger.LogWarning("Ignoring stale, duplicate or overflowing host entity ID. Epoch={Epoch}, Sequence={Sequence}, Key={Key}",
                    message.Epoch, message.Sequence, message.Key);
        }

        private async void OnNotifyRealTimeUnitKilled(long receivedFrom, NotifyRealTimeUnitKilled message)
        {
            if (receivedFrom != NetworkConstants.HostPlayerId || message == null
                || message.Sequence <= 0 || string.IsNullOrEmpty(message.UnitId))
                return;
            NetworkGame session = null;
            NetworkRealTimeCombatState state = null;
            NetworkCombat combat = null;
            await _realTimeSnapshotGate.WaitAsync();
            try
            {
                session = Game;
                state = session?.RealTimeCombat;
                combat = session?.Combat;
                if (string.IsNullOrWhiteSpace(message.SessionId)
                    || !string.Equals(message.SessionId, session?.Id, StringComparison.Ordinal)
                    || message.LoadedSaveSeed != session?.LoadedSaveSeed
                    || combat?.Stage != NetworkCombatStage.Running
                    || state?.Mode != CombatMode.RealTimeWithPause
                    || !(state.Epoch == message.Epoch
                        || state.PreviousMode == CombatMode.RealTimeWithPause
                            && state.Phase != CombatSynchronizationPhase.Stable
                            && state.Epoch == message.Epoch + 1))
                    return;
                if (!ReferenceEquals(_lastRealTimeDeathSession, session)
                    || _lastRealTimeDeathEpoch != message.Epoch
                    || _lastRealTimeDeathSaveSeed != message.LoadedSaveSeed)
                {
                    _lastRealTimeDeathSession = session;
                    _lastRealTimeDeathEpoch = message.Epoch;
                    _lastRealTimeDeathSaveSeed = message.LoadedSaveSeed;
                    _appliedRealTimeDeathSequences.Clear();
                }
                if (_appliedRealTimeDeathSequences.Contains(message.Sequence))
                    return;
                var player = GetPlayer(NetworkConstants.HostPlayerId);
                var applied = await CombatInteraction.TryApplyRealTimeUnitDeathAsync(
                    message.UnitId, player,
                    () => ReferenceEquals(Game, session) && ReferenceEquals(Game?.RealTimeCombat, state)
                        && ReferenceEquals(Game?.Combat, combat)
                        && session.LoadedSaveSeed == message.LoadedSaveSeed
                        && string.Equals(session.Id, message.SessionId, StringComparison.Ordinal)
                        && (state.Epoch == message.Epoch
                            || state.PreviousMode == CombatMode.RealTimeWithPause
                                && state.Phase != CombatSynchronizationPhase.Stable
                                && state.Epoch == message.Epoch + 1)
                        && combat.Stage == NetworkCombatStage.Running);
                if (!applied.HasValue)
                    return;
                if (!applied.Value)
                    throw new InvalidOperationException("host-unit-death-not-applied");
                _appliedRealTimeDeathSequences.Add(message.Sequence);
                Logger.LogInformation("Applied host real-time unit death. Epoch={Epoch}, Sequence={Sequence}, UnitId={UnitId}",
                    message.Epoch, message.Sequence, message.UnitId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to apply host real-time unit death. Epoch={Epoch}, Sequence={Sequence}, UnitId={UnitId}",
                    message.Epoch, message.Sequence, message.UnitId);
                if (!ReferenceEquals(Game, session) || !ReferenceEquals(Game?.RealTimeCombat, state)
                    || !ReferenceEquals(Game?.Combat, combat)
                    || session?.LoadedSaveSeed != message.LoadedSaveSeed
                    || !string.Equals(session?.Id, message.SessionId, StringComparison.Ordinal))
                    return;
                EnterLocalCriticalFaultBarrier(message.Epoch);
                Send(new ClientRealTimeCommandFault
                {
                    Epoch = message.Epoch,
                    AuthoritativeSequence = message.Sequence,
                    Reason = "host-unit-death-not-applied"
                });
            }
            finally
            {
                _realTimeSnapshotGate.Release();
            }
        }

        private NetworkGame _presentationSession;
        private NetworkRealTimeCombatState _presentationCombat;
        private long _presentationEpoch;
        private int _presentationSaveSeed;
        private RealTimePresentationInbox _presentationInbox = new();

        public void TickRealTimePresentation()
        {
            var active = RealTimePresentationRuntime.Enabled && IsRealTimeCombatActive
                && Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Stable;
            CombatInteraction.TickRealTimePresentation(active);
            if (active && ReferenceEquals(Game, _presentationSession)
                && ReferenceEquals(Game.RealTimeCombat, _presentationCombat)
                && _presentationEpoch == Game.RealTimeCombat.Epoch
                && _presentationSaveSeed == Game.LoadedSaveSeed
                && _presentationInbox.HasExpiredGap(DateTime.UtcNow))
                ReportRealTimeCommandFault(_presentationEpoch, _presentationInbox.AppliedSequence + 1,
                    "presentation-sequence-gap");
        }

        private async void OnNotifyRealTimeAuthoritativeEvent(long receivedFrom, NotifyRealTimeAuthoritativeEvent message)
        {
            if (!RealTimePresentationRuntime.Enabled || receivedFrom != NetworkConstants.HostPlayerId
                || message == null) return;
            await _realTimeSnapshotGate.WaitAsync();
            try
            {
                var session = Game;
                var combat = session?.RealTimeCombat;
                if (!IsRealTimeCombatActive || combat == null || combat.Phase != CombatSynchronizationPhase.Stable
                    || !IsCurrentRealTimeMessage(session, combat, message.Epoch)
                    || string.IsNullOrWhiteSpace(message.SessionId) || message.SessionId != session.Id
                    || message.LoadedSaveSeed != session.LoadedSaveSeed) return;
                if (!string.Equals(message.EventId, $"{message.Epoch}:{message.Sequence}", StringComparison.Ordinal)
                    || message.Kind != (int)RealTimeAuthoritativeEventKind.AttackPresentation
                    || message.Attack == null)
                {
                    ReportRealTimeCommandFault(message.Epoch, Math.Max(1, message.Sequence),
                        "malformed-authoritative-event");
                    return;
                }
                if (!ReferenceEquals(_presentationSession, session)
                    || !ReferenceEquals(_presentationCombat, combat) || _presentationEpoch != combat.Epoch
                    || _presentationSaveSeed != session.LoadedSaveSeed)
                {
                    _presentationSession = session;
                    _presentationCombat = combat;
                    _presentationEpoch = combat.Epoch;
                    _presentationSaveSeed = session.LoadedSaveSeed;
                    _presentationInbox = new RealTimePresentationInbox();
                }
                var inbox = _presentationInbox;
                if (!inbox.Enqueue(message))
                {
                    ReportRealTimeCommandFault(message.Epoch, Math.Max(1, message.Sequence), "presentation-window-overflow");
                    return;
                }
                while (inbox.TryPeek(out var next))
                {
                    Func<bool> canPresent = () => IsCurrentRealTimeMessage(session, combat, next.Epoch)
                        && IsRealTimeCombatActive && combat.Phase == CombatSynchronizationPhase.Stable
                        && next.SessionId == session.Id && next.LoadedSaveSeed == session.LoadedSaveSeed
                        && ReferenceEquals(inbox, _presentationInbox);
                    var applied = await CombatInteraction.TryPresentRealTimeAttackAsync(next, canPresent);
                    if (!canPresent()) return;
                    if (applied != true)
                    {
                        ReportRealTimeCommandFault(next.Epoch, next.Sequence, "attack-presentation-not-applied");
                        return;
                    }
                    inbox.Commit(next.Sequence);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Host attack presentation failed. Epoch={Epoch}, Sequence={Sequence}", message.Epoch, message.Sequence);
                if (ReferenceEquals(Game, _presentationSession))
                    ReportRealTimeCommandFault(message.Epoch, Math.Max(1, message.Sequence), "attack-presentation-exception");
            }
            finally { _realTimeSnapshotGate.Release(); }
        }

        private async void OnNotifyRealTimeCombatCommandAccepted(long receivedFrom, NotifyRealTimeCombatCommandAccepted message)
        {
            if (receivedFrom != NetworkConstants.HostPlayerId || message == null) return;
            await _realTimeSnapshotGate.WaitAsync();
            NetworkGame session = null;
            NetworkRealTimeCombatState combat = null;
            try
            {
                if (Game?.RealTimeCombat?.Mode != CombatMode.RealTimeWithPause
                    || !string.Equals(message.SessionId, Game.Id, StringComparison.Ordinal)
                    || message.LoadedSaveSeed != Game.LoadedSaveSeed
                    || Game.Combat?.Stage != NetworkCombatStage.Running
                    || !new RealTimeCombatCoordinator(Game.RealTimeCombat).IsCurrent(message.Epoch, message.AuthoritativeSequence))
                {
                    return;
                }

                session = Game;
                combat = session.RealTimeCombat;
                if (RealTimePresentationRuntime.Enabled)
                {
                    if (!IsCurrentRealTimeMessage(session, combat, message.Epoch)
                        || combat.Phase != CombatSynchronizationPhase.Stable) return;
                    var valid = (RealTimeCommandKind)message.Kind switch
                    {
                        RealTimeCommandKind.Move => message.Movement != null,
                        RealTimeCommandKind.Attack => message.Attack != null,
                        RealTimeCommandKind.UseAbility => message.Ability != null,
                        _ => false
                    };
                    if (!valid)
                    {
                        ReportRealTimeCommandFault(message.Epoch, message.AuthoritativeSequence,
                            "malformed-authoritative-command");
                        return;
                    }
                    // Acceptance is not permission to replay a combat command locally.
                    // Position/health come from host checkpoints; animation has its own lane.
                    combat.LastAuthoritativeSequence = message.AuthoritativeSequence;
                    RealTimeAcceptedOrderLedger.Record(combat, message);
                    return;
                }
                Func<bool> canExecute = () => IsCurrentRealTimeMessage(session, combat, message.Epoch)
                    && session.Combat?.Stage == NetworkCombatStage.Running
                    && new RealTimeCombatCoordinator(combat).IsCurrent(message.Epoch, message.AuthoritativeSequence);
                Task<bool?> execution;
                switch ((RealTimeCommandKind)message.Kind)
                {
                    case RealTimeCommandKind.Move when message.Movement != null:
                        execution = CombatInteraction.TryExecuteRealTimeCombatCommandAsync(RealTimeCommandKind.Move,
                            Mapper.Map<NetworkUnitMoveTo>(message.Movement), null, null, canExecute);
                        break;
                    case RealTimeCommandKind.Attack when message.Attack != null:
                        execution = CombatInteraction.TryExecuteRealTimeCombatCommandAsync(RealTimeCommandKind.Attack,
                            null, Mapper.Map<NetworkUnitAttack>(message.Attack), null, canExecute);
                        break;
                    case RealTimeCommandKind.UseAbility when message.Ability != null:
                        execution = CombatInteraction.TryExecuteRealTimeCombatCommandAsync(RealTimeCommandKind.UseAbility,
                            null, null, Mapper.Map<NetworkAbilityUse>(message.Ability), canExecute);
                        break;
                    default:
                        ReportRealTimeCommandFault(message.Epoch, message.AuthoritativeSequence,
                            "malformed-authoritative-command");
                        return;
                }

                var applied = await execution;
                if (!canExecute())
                    return;
                if (applied != true)
                {
                    ReportRealTimeCommandFault(message.Epoch, message.AuthoritativeSequence,
                        "authoritative-command-rejected");
                    return;
                }
                combat.LastAuthoritativeSequence = message.AuthoritativeSequence;
                RealTimeAcceptedOrderLedger.Record(combat, message);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to apply authoritative real-time command. Epoch={Epoch}, Sequence={Sequence}", message.Epoch, message.AuthoritativeSequence);
                if (session != null && combat != null
                    && IsCurrentRealTimeMessage(session, combat, message.Epoch))
                    ReportRealTimeCommandFault(message.Epoch, message.AuthoritativeSequence,
                        "authoritative-command-exception");
            }
            finally
            {
                _realTimeSnapshotGate.Release();
            }
        }

        private void OnNotifyRealTimeCombatCommandRejected(long receivedFrom,
            NotifyRealTimeCombatCommandRejected message)
        {
            if (receivedFrom != NetworkConstants.HostPlayerId || message == null || Game == null
                || message.PlayerId != Game.LocalPlayerId || message.Epoch != Game.RealTimeCombat?.Epoch
                || !string.Equals(message.SessionId, Game.Id, StringComparison.Ordinal)
                || message.LoadedSaveSeed != Game.LoadedSaveSeed) return;
            var rejection = Enum.IsDefined(typeof(RealTimeCommandRejection), message.Reason)
                ? (RealTimeCommandRejection)message.Reason : (RealTimeCommandRejection)(-1);
            var reasonKey = rejection switch
            {
                RealTimeCommandRejection.RealTimeModeInactive => WellKnownKeys.GameNotifications.Combat.RealTimeCommandRejection.Inactive.Key,
                RealTimeCommandRejection.SynchronizationBarrierActive => WellKnownKeys.GameNotifications.Combat.RealTimeCommandRejection.Barrier.Key,
                RealTimeCommandRejection.WrongEpoch => WellKnownKeys.GameNotifications.Combat.RealTimeCommandRejection.Epoch.Key,
                RealTimeCommandRejection.InvalidSequence => WellKnownKeys.GameNotifications.Combat.RealTimeCommandRejection.Sequence.Key,
                RealTimeCommandRejection.SenderMismatch => WellKnownKeys.GameNotifications.Combat.RealTimeCommandRejection.Sender.Key,
                RealTimeCommandRejection.WrongSession => WellKnownKeys.GameNotifications.Combat.RealTimeCommandRejection.Session.Key,
                RealTimeCommandRejection.WrongLoadedSave => WellKnownKeys.GameNotifications.Combat.RealTimeCommandRejection.Save.Key,
                RealTimeCommandRejection.InvalidCommandId => WellKnownKeys.GameNotifications.Combat.RealTimeCommandRejection.CommandId.Key,
                RealTimeCommandRejection.MalformedCommand => WellKnownKeys.GameNotifications.Combat.RealTimeCommandRejection.Malformed.Key,
                RealTimeCommandRejection.UnknownUnit => WellKnownKeys.GameNotifications.Combat.RealTimeCommandRejection.UnknownUnit.Key,
                RealTimeCommandRejection.UnitOwnedByAnotherPlayer => WellKnownKeys.GameNotifications.Combat.RealTimeCommandRejection.Ownership.Key,
                RealTimeCommandRejection.GameExecutionRejected => WellKnownKeys.GameNotifications.Combat.RealTimeCommandRejection.Game.Key,
                RealTimeCommandRejection.StateChangedDuringExecution => WellKnownKeys.GameNotifications.Combat.RealTimeCommandRejection.StateChanged.Key,
                _ => WellKnownKeys.GameNotifications.Combat.RealTimeCommandRejection.Unknown.Key
            };
            Logger.LogWarning("Host rejected real-time command. CommandId={CommandId}, Reason={Reason}",
                message.CommandId, rejection);
            PlayerNotification.ShowWarningNotification(
                WellKnownKeys.GameNotifications.Combat.RealTimeCommandRejected.Key,
                args: new Kingmaker.Localization.LocalizedString { Key = reasonKey }.ToString());
        }

        private void ReportRealTimeCommandFault(long epoch, long sequence, string reason)
        {
            if (!IsRealTimeCombatActive || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable
                || Game.RealTimeCombat.Epoch != epoch || sequence <= 0)
                return;
            Logger.LogError("Authoritative real-time command cannot be applied. Epoch={Epoch}, Sequence={Sequence}, Reason={Reason}",
                epoch, sequence, reason);
            EnterLocalCriticalFaultBarrier(epoch);
            Send(new ClientRealTimeCommandFault
            {
                Epoch = epoch, AuthoritativeSequence = sequence, Reason = reason
            });
        }

        private void OnNotifyRealTimePauseChanged(long receivedFrom, NotifyRealTimePauseChanged message)
        {
            if (receivedFrom != NetworkConstants.HostPlayerId || Game?.RealTimeCombat == null || message == null
                || message.Epoch != Game.RealTimeCombat.Epoch
                || message.Sequence <= Game.RealTimeCombat.PauseSequence
                || message.IsPaused != (message.PausedPlayers?.Count > 0)
                || !Enum.IsDefined(typeof(PausePolicy), message.Policy))
            {
                return;
            }

            Game.RealTimeCombat.PauseSequence = message.Sequence;
            Game.RealTimeCombat.PausePolicy = (PausePolicy)message.Policy;
            Game.RealTimeCombat.PausedPlayers = [.. message.PausedPlayers];
            Game.RealTimeCombat.ResumeVotes = [.. message.ResumeVotes ?? []];
            if (Game.RealTimeCombat.Mode == CombatMode.RealTimeWithPause
                && Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Stable)
            {
                GameInteraction.SetPause(message.IsPaused || Game.ForcedPause != null);
            }
        }

        private void OnNotifyRealTimeCombatPreference(long receivedFrom, NotifyRealTimeCombatPreference message)
        {
            if (receivedFrom != NetworkConstants.HostPlayerId || Game?.RealTimeCombat == null || message == null
                || message.Sequence <= Game.RealTimeCombat.PreferenceSequence
                || !Enum.IsDefined(typeof(CombatMode), message.PreferredMode)
                || !Enum.IsDefined(typeof(PausePolicy), message.PausePolicy))
            {
                return;
            }

            Game.RealTimeCombat.PreferenceSequence = message.Sequence;
            Game.RealTimeCombat.PreferredMode = (CombatMode)message.PreferredMode;
            Game.RealTimeCombat.PausePolicy = (PausePolicy)message.PausePolicy;
        }

        private async void OnNotifyRealTimeModeTransition(long receivedFrom, NotifyRealTimeModeTransition message)
        {
            if (receivedFrom != NetworkConstants.HostPlayerId) return;
            await _realTimeSnapshotGate.WaitAsync();
            NetworkGame session = null;
            NetworkRealTimeCombatState state = null;
            try
            {
                if (Game?.RealTimeCombat == null || message == null
                    || !Enum.IsDefined(typeof(CombatMode), message.Mode)
                    || !Enum.IsDefined(typeof(RealTimeModeTransitionStage), message.Stage))
                {
                    return;
                }

                session = Game;
                state = session.RealTimeCombat;
                var stage = (RealTimeModeTransitionStage)message.Stage;
                var mode = (CombatMode)message.Mode;
                if (message.Epoch < state.Epoch)
                {
                    return;
                }

                if (stage == RealTimeModeTransitionStage.Prepare)
                {
                    if (message.Epoch == state.Epoch
                        && state.Phase == CombatSynchronizationPhase.PreparingModeSwitch
                        && state.PendingMode == mode)
                    {
                        SendRealTimeModeAck(message, true, 0);
                        return;
                    }
                    if (message.Epoch <= state.Epoch || state.Phase != CombatSynchronizationPhase.Stable)
                    {
                        return;
                    }
                    state.PreviousMode = state.Mode;
                    state.PendingMode = mode;
                    state.Epoch = message.Epoch;
                    state.LastAuthoritativeSequence = 0;
                    ResetRealTimeAIChoices(message.Epoch);
                    state.Phase = CombatSynchronizationPhase.PreparingModeSwitch;
                    var paused = await PauseClientAtProjectileSafePointAsync(session, state, message.Epoch);
                    if (!IsCurrentRealTimeMessage(session, state, message.Epoch)
                        || state.Phase != CombatSynchronizationPhase.PreparingModeSwitch)
                        return;
                    SendRealTimeModeAck(message, paused, 0);
                    return;
                }

                // The full abort checkpoint must also recover a peer that never received Prepare.
                if (stage == RealTimeModeTransitionStage.Abort && message.Epoch > state.Epoch
                    && message.State != null)
                {
                    state.Epoch = message.Epoch;
                    state.LastAuthoritativeSequence = 0;
                    ResetRealTimeAIChoices(message.Epoch);
                }

                if (message.Epoch != state.Epoch)
                {
                    return;
                }

                if (stage == RealTimeModeTransitionStage.Commit)
                {
                    if (state.Phase == CombatSynchronizationPhase.Synchronizing
                        && state.Mode == mode
                        && state.CanonicalStateHash == message.CanonicalStateHash)
                    {
                        SendRealTimeModeAck(message, true, state.CanonicalStateHash);
                        return;
                    }
                    if (state.Phase != CombatSynchronizationPhase.PreparingModeSwitch
                        || state.PendingMode != mode || message.State == null)
                    {
                        return;
                    }
                    var actualHash = await ApplyRealTimeModeSnapshotAsync(message.State,
                        session, state, message.Epoch, CombatSynchronizationPhase.PreparingModeSwitch);
                    if (!IsCurrentRealTimeMessage(session, state, message.Epoch)
                        || state.Phase != CombatSynchronizationPhase.PreparingModeSwitch
                        || !actualHash.HasValue)
                        return;
                    if (actualHash != message.CanonicalStateHash)
                    {
                        Logger.LogWarning("Combat-mode commit checkpoint differs. Epoch={Epoch}, HostHash={HostHash}, ClientHash={ClientHash}", message.Epoch, message.CanonicalStateHash, actualHash);
                        SendRealTimeModeAck(message, false, actualHash.Value);
                        return;
                    }
                    if (!await GameInteraction.TrySetTurnBasedCombatModeAsync(mode == CombatMode.TurnBased,
                            () => IsCurrentRealTimeMessage(session, state, message.Epoch)
                                && state.Phase == CombatSynchronizationPhase.PreparingModeSwitch))
                        return;
                    if (!IsCurrentRealTimeMessage(session, state, message.Epoch)
                        || state.Phase != CombatSynchronizationPhase.PreparingModeSwitch)
                        return;
                    state.Mode = mode;
                    state.CanonicalStateHash = actualHash.Value;
                    state.Phase = CombatSynchronizationPhase.Synchronizing;
                    SendRealTimeModeAck(message, true, actualHash.Value);
                    return;
                }

                if (stage == RealTimeModeTransitionStage.Abort)
                {
                    if (message.State == null)
                    {
                        return;
                    }
                    state.Phase = CombatSynchronizationPhase.Repairing;
                    await GameInteraction.SetPauseAsync(true);
                    if (!IsCurrentRealTimeMessage(session, state, message.Epoch)
                        || state.Phase != CombatSynchronizationPhase.Repairing)
                        return;
                    var actualHash = await ApplyRealTimeModeSnapshotAsync(message.State,
                        session, state, message.Epoch, CombatSynchronizationPhase.Repairing);
                    if (!IsCurrentRealTimeMessage(session, state, message.Epoch)
                        || state.Phase != CombatSynchronizationPhase.Repairing
                        || !actualHash.HasValue)
                        return;
                    if (!await GameInteraction.TrySetTurnBasedCombatModeAsync(mode == CombatMode.TurnBased,
                            () => IsCurrentRealTimeMessage(session, state, message.Epoch)
                                && state.Phase == CombatSynchronizationPhase.Repairing))
                        return;
                    if (!IsCurrentRealTimeMessage(session, state, message.Epoch)
                        || state.Phase != CombatSynchronizationPhase.Repairing)
                        return;
                    state.Mode = mode;
                    state.PendingMode = null;
                    state.PreviousMode = null;
                    state.CanonicalStateHash = message.CanonicalStateHash;
                    SendRealTimeModeAck(message, actualHash == message.CanonicalStateHash, actualHash.Value);
                    return;
                }

                if (stage == RealTimeModeTransitionStage.Complete
                    && (state.Phase == CombatSynchronizationPhase.Synchronizing
                        || state.Phase == CombatSynchronizationPhase.Repairing)
                    && state.Mode == mode && state.CanonicalStateHash == message.CanonicalStateHash)
                {
                    if (mode == CombatMode.RealTimeWithPause
                        && ReferenceEquals(_localCriticalFaultState, state)
                        && _localCriticalFaultEpoch == message.Epoch)
                        return;
                    state.PendingMode = null;
                    state.PreviousMode = null;
                    state.Phase = CombatSynchronizationPhase.Stable;
                    if (mode == CombatMode.TurnBased)
                    {
                        _disconnectedDuringRealTimeCombat = false;
                        _localCriticalFaultState = null;
                    }
                    SendRealTimeModeAck(message, true, state.CanonicalStateHash);
                    GameInteraction.SetPause(state.IsManualPauseActive || Game.ForcedPause != null);
                    return;
                }
                if (stage == RealTimeModeTransitionStage.Complete
                    && state.Phase == CombatSynchronizationPhase.Stable
                    && state.Mode == mode && state.CanonicalStateHash == message.CanonicalStateHash)
                {
                    SendRealTimeModeAck(message, true, state.CanonicalStateHash);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to apply combat-mode transition. Epoch={Epoch}, Stage={Stage}", message?.Epoch, message?.Stage);
                if (message != null && session != null && state != null
                    && IsCurrentRealTimeMessage(session, state, message.Epoch))
                {
                    SendRealTimeModeAck(message, false, 0);
                }
            }
            finally
            {
                _realTimeSnapshotGate.Release();
            }
        }

        private async Task<ulong?> ApplyRealTimeModeSnapshotAsync(
            Networking.Messages.Contracts.NetworkCombatState snapshot,
            NetworkGame session, NetworkRealTimeCombatState state, long epoch,
            CombatSynchronizationPhase phase)
        {
            var expected = Mapper.Map<NetworkCombatState>(snapshot);
            var appliedState = await CombatInteraction.TryApplyAndReadRealTimeCombatStateAsync(expected,
                () => IsCurrentRealTimeMessage(session, state, epoch) && state.Phase == phase);
            if (appliedState == null)
                return null;
            var temporalMismatch = RealTimeCombatTemporalDiff.Summarize(expected, appliedState);
            if (temporalMismatch != null)
                throw new InvalidOperationException($"Temporal combat checkpoint differs: {temporalMismatch}");
            return RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = appliedState });
        }

        private bool IsCurrentRealTimeMessage(NetworkGame session, NetworkRealTimeCombatState state, long epoch)
        {
            return ReferenceEquals(Game, session)
                && ReferenceEquals(session.RealTimeCombat, state)
                && state.Epoch == epoch;
        }

        private async Task<bool> PauseClientAtProjectileSafePointAsync(
            NetworkGame session, NetworkRealTimeCombatState state, long epoch)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                Func<bool> canPause = () => IsCurrentRealTimeMessage(session, state, epoch)
                    && state.Phase == CombatSynchronizationPhase.PreparingModeSwitch;
                if (!canPause())
                    return false;
                if (await CombatInteraction.TryPauseAtProjectileSafePointAsync(canPause,
                    () => GameInteraction.SetPause(true)))
                    return canPause();
                if (!canPause())
                    return false;
                await Task.Delay(50);
            }
            Logger.LogWarning("Client could not reach a projectile-and-action-safe combat-mode pause within 10 seconds. Epoch={Epoch}",
                epoch);
            return false;
        }

        private void SendRealTimeModeAck(NotifyRealTimeModeTransition message, bool applied, ulong hash)
        {
            Send(new ClientRealTimeModeTransitionAck
            {
                Epoch = message.Epoch,
                Stage = message.Stage,
                PlayerId = Game.LocalPlayerId,
                CanonicalStateHash = hash,
                Applied = applied
            });
        }

        private async void OnNotifyRealTimeRepair(long receivedFrom, NotifyRealTimeRepair message)
        {
            if (receivedFrom != NetworkConstants.HostPlayerId) return;
            await _realTimeSnapshotGate.WaitAsync();
            NetworkGame session = null;
            NetworkRealTimeCombatState state = null;
            try
            {
                if (Game?.RealTimeCombat == null || message == null
                    || !Enum.IsDefined(typeof(RealTimeRepairStage), message.Stage)
                    || !Enum.IsDefined(typeof(CombatMode), message.Mode))
                {
                    return;
                }
                session = Game;
                state = session.RealTimeCombat;
                var stage = (RealTimeRepairStage)message.Stage;
                if (stage == RealTimeRepairStage.Prepare)
                {
                    if (message.Epoch == state.Epoch
                        && state.Phase == CombatSynchronizationPhase.Repairing
                        && state.Mode == CombatMode.RealTimeWithPause)
                    {
                        SendRealTimeRepairAck(message, true, 0);
                        return;
                    }
                    if (message.Epoch <= state.Epoch || state.Mode != CombatMode.RealTimeWithPause
                        || (state.Phase != CombatSynchronizationPhase.Stable
                            && state.Phase != CombatSynchronizationPhase.Repairing))
                    {
                        return;
                    }
                    state.Epoch = message.Epoch;
                    state.LastAuthoritativeSequence = 0;
                    _localCriticalFaultState = null;
                    ResetRealTimeAIChoices(message.Epoch);
                    state.Phase = CombatSynchronizationPhase.Repairing;
                    await GameInteraction.SetPauseAsync(true);
                    if (!IsCurrentRealTimeMessage(session, state, message.Epoch)
                        || state.Phase != CombatSynchronizationPhase.Repairing)
                        return;
                    SendRealTimeRepairAck(message, true, 0);
                    return;
                }
                // A complete fallback checkpoint remains valid if an earlier repair message was lost.
                if (stage == RealTimeRepairStage.Fallback && message.Epoch > state.Epoch
                    && state.Mode == CombatMode.RealTimeWithPause && message.State != null)
                {
                    state.Epoch = message.Epoch;
                    state.LastAuthoritativeSequence = 0;
                    ResetRealTimeAIChoices(message.Epoch);
                    state.Phase = CombatSynchronizationPhase.Repairing;
                    await GameInteraction.SetPauseAsync(true);
                    if (!IsCurrentRealTimeMessage(session, state, message.Epoch)
                        || state.Phase != CombatSynchronizationPhase.Repairing)
                        return;
                }
                if (message.Epoch != state.Epoch)
                {
                    return;
                }
                if (stage == RealTimeRepairStage.Verify
                    && state.Phase == CombatSynchronizationPhase.Repairing)
                {
                    var local = await CombatInteraction.GetRealTimeCombatStateAsync();
                    if (!IsCurrentRealTimeMessage(session, state, message.Epoch)
                        || state.Phase != CombatSynchronizationPhase.Repairing)
                        return;
                    var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = local });
                    var expected = message.State == null ? null : Mapper.Map<NetworkCombatState>(message.State);
                    var temporalMismatch = expected == null ? "missing-verify-checkpoint"
                        : RealTimeCombatTemporalDiff.Summarize(expected, local);
                    if (temporalMismatch != null)
                        Logger.LogWarning("Temporal combat verification differs. Epoch={Epoch}, Reason={Reason}",
                            message.Epoch, temporalMismatch);
                    SendRealTimeRepairAck(message, true, hash, temporalMismatch != null);
                    return;
                }
                if (stage == RealTimeRepairStage.Apply
                    && state.Phase == CombatSynchronizationPhase.Repairing && message.State != null)
                {
                    var hash = await ApplyRealTimeModeSnapshotAsync(message.State,
                        session, state, message.Epoch, CombatSynchronizationPhase.Repairing);
                    if (!IsCurrentRealTimeMessage(session, state, message.Epoch)
                        || state.Phase != CombatSynchronizationPhase.Repairing
                        || !hash.HasValue)
                        return;
                    state.CanonicalStateHash = message.CanonicalStateHash;
                    SendRealTimeRepairAck(message, hash == message.CanonicalStateHash, hash.Value);
                    return;
                }
                if (stage == RealTimeRepairStage.Fallback
                    && (state.Phase == CombatSynchronizationPhase.Repairing
                        || state.Phase == CombatSynchronizationPhase.FallingBackToTurnBased)
                    && message.State != null)
                {
                    state.Phase = CombatSynchronizationPhase.FallingBackToTurnBased;
                    var hash = await ApplyRealTimeModeSnapshotAsync(message.State,
                        session, state, message.Epoch, CombatSynchronizationPhase.FallingBackToTurnBased);
                    if (!IsCurrentRealTimeMessage(session, state, message.Epoch)
                        || state.Phase != CombatSynchronizationPhase.FallingBackToTurnBased
                        || !hash.HasValue)
                        return;
                    if (!await GameInteraction.TrySetTurnBasedCombatModeAsync(true,
                            () => IsCurrentRealTimeMessage(session, state, message.Epoch)
                                && state.Phase == CombatSynchronizationPhase.FallingBackToTurnBased))
                        return;
                    if (!IsCurrentRealTimeMessage(session, state, message.Epoch)
                        || state.Phase != CombatSynchronizationPhase.FallingBackToTurnBased)
                        return;
                    state.CanonicalStateHash = message.CanonicalStateHash;
                    SendRealTimeRepairAck(message, hash == message.CanonicalStateHash, hash.Value);
                    return;
                }
                if (stage == RealTimeRepairStage.Resume
                    && (state.Phase == CombatSynchronizationPhase.Repairing
                        || state.Phase == CombatSynchronizationPhase.FallingBackToTurnBased))
                {
                    if ((CombatMode)message.Mode == CombatMode.RealTimeWithPause
                        && ReferenceEquals(_localCriticalFaultState, state)
                        && _localCriticalFaultEpoch == message.Epoch)
                        return;
                    state.Mode = (CombatMode)message.Mode;
                    state.CanonicalStateHash = message.CanonicalStateHash;
                    state.Phase = CombatSynchronizationPhase.Stable;
                    if (state.Mode == CombatMode.TurnBased)
                    {
                        _disconnectedDuringRealTimeCombat = false;
                        _localCriticalFaultState = null;
                    }
                    SendRealTimeRepairAck(message, true, state.CanonicalStateHash);
                    GameInteraction.SetPause(state.IsManualPauseActive || Game.ForcedPause != null);
                    return;
                }
                if (stage == RealTimeRepairStage.Resume
                    && state.Phase == CombatSynchronizationPhase.Stable
                    && state.Mode == (CombatMode)message.Mode
                    && state.CanonicalStateHash == message.CanonicalStateHash)
                {
                    SendRealTimeRepairAck(message, true, state.CanonicalStateHash);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to apply real-time combat repair. Epoch={Epoch}, Stage={Stage}", message?.Epoch, message?.Stage);
                if (message != null && session != null && state != null
                    && IsCurrentRealTimeMessage(session, state, message.Epoch))
                {
                    SendRealTimeRepairAck(message, false, 0);
                }
            }
            finally
            {
                _realTimeSnapshotGate.Release();
            }
        }

        private void SendRealTimeRepairAck(NotifyRealTimeRepair message, bool applied, ulong hash,
            bool temporalMismatch = false)
        {
            Send(new ClientRealTimeRepairAck
            {
                Epoch = message.Epoch,
                Stage = message.Stage,
                PlayerId = Game.LocalPlayerId,
                CanonicalStateHash = hash,
                Applied = applied,
                TemporalMismatch = temporalMismatch
            });
        }

        private async void OnNotifyCombatLocalTurnEnded(long receivedFrom, NotifyCombatLocalTurnEnded message)
        {
            if (!CombatTurnMessageGate.IsCurrentEndMessage(Game?.Combat?.Turn?.UnitId, message.UnitId, receivedFrom, message.PlayerId, validateSender: false))
            {
                Logger.LogWarning("Ignoring stale turn-end notification. MessageUnitId={MessageUnitId}, CurrentUnitId={CurrentUnitId}", message.UnitId, Game?.Combat?.Turn?.UnitId);
                return;
            }

            if (Game.Combat.Turn.IsAI)
            {
                return;
            }

            await WaitForTurnToBeCompletedAsync();
            if (!CombatTurnMessageGate.IsCurrentEndMessage(Game?.Combat?.Turn?.UnitId, message.UnitId, receivedFrom, message.PlayerId, validateSender: false))
            {
                Logger.LogWarning("Turn changed while completing remote action; stale notification discarded. MessageUnitId={MessageUnitId}, CurrentUnitId={CurrentUnitId}", message.UnitId, Game?.Combat?.Turn?.UnitId);
                return;
            }
            CombatInteraction.EndCombatTurn(message.UnitId);
        }

        private async void OnNotifyAIActionExecuted(long receivedFrom, NotifyAIActionSelected message)
        {
            if (Game.RealTimeCombat?.Mode == CombatMode.RealTimeWithPause
                && receivedFrom != NetworkConstants.HostPlayerId)
            {
                return;
            }
            if (Game.ArmyCombat != null)
            {
                return;
            }

            if (Game.RealTimeCombat?.Mode == CombatMode.RealTimeWithPause)
            {
                if (message?.Action == null || message.RealTimeEpoch != Game.RealTimeCombat.Epoch
                    || message.RealTimeSequence <= 0
                    || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable)
                {
                    return;
                }
                var authoritativeAction = Mapper.Map<NetworkAIAction>(message.Action);
                if (authoritativeAction == null || string.IsNullOrWhiteSpace(authoritativeAction.UnitId))
                {
                    ReportRealTimeAIActionFault(null, null, "host-choice-unit-missing");
                    return;
                }
                var overflow = false;
                long gapExpectedSequence = 0;
                long gapGeneration = 0;
                NetworkGame gapSession = null;
                lock (_realTimeAIChoiceLock)
                {
                    if (Game.RealTimeCombat?.Mode != CombatMode.RealTimeWithPause
                        || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable
                        || Game.RealTimeCombat.Epoch != message.RealTimeEpoch)
                        return;
                    if (_realTimeAISequenceEpoch != message.RealTimeEpoch)
                        ResetRealTimeAIChoices(message.RealTimeEpoch);
                    if (message.RealTimeSequence <= _lastRealTimeAISequence
                        || _pendingRealTimeAIChoices.ContainsKey(message.RealTimeSequence))
                        return;
                    if (message.RealTimeSequence - _lastRealTimeAISequence > MaxPendingRealTimeAIChoices
                        || _pendingRealTimeAIChoices.Count >= MaxPendingRealTimeAIChoices)
                    {
                        overflow = true;
                    }
                    else
                    {
                        _pendingRealTimeAIChoices.Add(message.RealTimeSequence, authoritativeAction);
                        while (_pendingRealTimeAIChoices.TryGetValue(_lastRealTimeAISequence + 1, out var next))
                        {
                            _pendingRealTimeAIChoices.Remove(++_lastRealTimeAISequence);
                            _realTimeAIActions.GetOrAdd(next.UnitId,
                                _ => new ConcurrentQueue<NetworkAIAction>()).Enqueue(next);
                        }
                        var expected = _pendingRealTimeAIChoices.Count > 0
                            ? _lastRealTimeAISequence + 1 : 0;
                        if (expected != _realTimeAIChoiceGapExpectedSequence)
                        {
                            _realTimeAIChoiceGapExpectedSequence = expected;
                            gapExpectedSequence = expected;
                            gapGeneration = _realTimeAIChoiceGeneration;
                            gapSession = Game;
                        }
                    }
                }
                if (overflow)
                    ReportRealTimeAIActionFault(authoritativeAction.UnitId, authoritativeAction.Id,
                        "host-choice-sequence-overflow");
                if (gapExpectedSequence > 0)
                    _ = CheckRealTimeAIChoiceGapAsync(gapSession, message.RealTimeEpoch,
                        gapExpectedSequence, gapGeneration);
                return;
            }

            await WaitWhileTrue(() => Game.Combat.Turn == null, "Waiting for turn to initialize before saving AI actions");

            var aiAction = Mapper.Map<NetworkAIAction>(message.Action);
            Game.Combat.Turn.AIActions.Add(aiAction);
        }

        private async Task CheckRealTimeAIChoiceGapAsync(NetworkGame session, long epoch,
            long expectedSequence, long generation)
        {
            await Task.Delay(RealTimeAIChoiceGapTimeoutMs);
            NetworkRealTimeCombatState state;
            lock (_realTimeAIChoiceLock)
            {
                state = session?.RealTimeCombat;
                if (!ReferenceEquals(Game, session)
                    || state?.Mode != CombatMode.RealTimeWithPause
                    || state.Phase != CombatSynchronizationPhase.Stable
                    || state.Epoch != epoch
                    || _realTimeAISequenceEpoch != epoch
                    || _realTimeAIChoiceGeneration != generation
                    || _realTimeAIChoiceGapExpectedSequence != expectedSequence
                    || _pendingRealTimeAIChoices.Count == 0)
                    return;
                _localCriticalFaultState = state;
                _localCriticalFaultEpoch = epoch;
                state.Phase = CombatSynchronizationPhase.Repairing;
            }
            Logger.LogError("Host real-time AI choice sequence gap persisted. Epoch={Epoch}, ExpectedSequence={ExpectedSequence}",
                epoch, expectedSequence);
            try
            {
                await GameInteraction.SetPauseAsync(true);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to pause client after a host AI choice sequence gap. Epoch={Epoch}", epoch);
            }
            if (ReferenceEquals(Game, session) && ReferenceEquals(session.RealTimeCombat, state)
                && state.Epoch == epoch)
            {
                try
                {
                    Send(new ClientRealTimeAIActionFault
                    {
                        Epoch = epoch, Reason = "host-choice-sequence-gap"
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Unable to report host AI choice sequence gap. Epoch={Epoch}", epoch);
                }
            }
        }

        private async void OnNotifyGlobalMapCommonPopupShown(long receivedFrom, NotifyGlobalMapCommonPopupShown message)
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapCommonPopup, message.PlayerId);

            var popup = Mapper.Map<NetworkGlobalMapCommonPopup>(message.Popup);

            var isShown = await GlobalMapInteraction.ShowCommonPopupAsync(popup);
            if (isShown)
            {
                OnGlobalMapCommonPopupShown(popup);
                return;
            }

            UpdateGlobalMapCommonPopupUIState(popup);
        }

        private void OnNotifyGlobalMapCrusadeArmyLeaderLevelingSkillSelected(long receivedFrom, NotifyGlobalMapCrusadeArmyLeaderLevelingSkillSelected message)
        {
            GlobalMapInteraction.SelectLeaderLevelingSkill(message.Id);
        }

        private void OnNotifyGlobalMapCrusadeArmyLeaderLevelingConfirmed(long receivedFrom, NotifyGlobalMapCrusadeArmyLeaderLevelingConfirmed message)
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmyLeaderLeveling);

            GlobalMapInteraction.ConfirmLeaderLeveling();
        }

        private void OnNotifyGlobalMapCrusadeArmyLeaderLevelingClosed(long receivedFrom, NotifyGlobalMapCrusadeArmyLeaderLevelingClosed message)
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmyLeaderLeveling);

            GlobalMapInteraction.CloseLeaderLeveling();
        }

        private void OnNotifyGlobalMapMagicSpellUsed(long receivedFrom, NotifyGlobalMapMagicSpellUsed message)
        {
            var spell = Mapper.Map<NetworkGlobalMapMagicSpell>(message.Spell);

            GlobalMapInteraction.UseSpell(spell);
        }

        private void OnNotifyGlobalMapCrusadeArmyRecruitCartClosed(long receivedFrom, NotifyGlobalMapCrusadeArmyRecruitCartClosed message)
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapRecruitment);
            GlobalMapInteraction.CloseRecruitments();
        }

        private void OnNotifyGlobalMapCrusadeArmyDismissed(long receivedFrom, NotifyGlobalMapCrusadeArmyDismissed message)
        {
            var army = Mapper.Map<NetworkGlobalMapArmy>(message.Army);

            GlobalMapInteraction.DismissCrusadeArmy(army);
        }

        private void OnNotifyGlobalMapRecruitmentClosed(long receivedFrom, NotifyGlobalMapRecruitmentClosed message)
        {
            // no need for specific removal as recruitment is already closed
            ResetPlayersTracker(Game.PlayersInGlobalMapRecruitment);

            GlobalMapInteraction.CloseRecruitments();
        }

        private void OnNotifyGlobalMapRecruitmentShown(long receivedFrom, NotifyGlobalMapRecruitmentShown message)
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapRecruitment, message.PlayerId);
            UpdateGlobalMapRecruitmentUIState();

            GlobalMapInteraction.OpenRecruitments();
        }

        private void OnNotifyGlobalMapResourcesBought(long receivedFrom, NotifyGlobalMapResourcesBought message)
        {
            var globalMapResourceOrder = Mapper.Map<NetworkGlobalMapResourceOrder>(message.Order);

            GlobalMapInteraction.BuyResources(globalMapResourceOrder);
        }

        private void OnNotifyGlobalMapUnitsRecruited(long receivedFrom, NotifyGlobalMapUnitsRecruited message)
        {
            var globalMapUnitRecruitmentOrder = Mapper.Map<NetworkGlobalMapUnitRecruitmentOrder>(message.Order);

            GlobalMapInteraction.BuyUnits(globalMapUnitRecruitmentOrder);
        }

        private void OnNotifyGlobalMapRecruitmentPrevArmySelected(long receivedFrom, NotifyGlobalMapRecruitmentPrevArmySelected message)
        {
            GlobalMapInteraction.SelectPrevRecruitmentArmy();
        }

        private void OnNotifyGlobalMapRecruitmentNextArmySelected(long receivedFrom, NotifyGlobalMapRecruitmentNextArmySelected message)
        {
            GlobalMapInteraction.SelectNextRecruitmentArmy();
        }

        private void OnNotifyGlobalMapRecruitmentMercenariesRerolled(long receivedFrom, NotifyGlobalMapRecruitmentMercenariesRerolled message)
        {
            GlobalMapInteraction.RerollRecruitmentMercenaries();
        }

        private void OnNotifyGlobalMapCrusadeArmyBuyLeaderClosed(long receivedFrom, NotifyGlobalMapCrusadeArmyBuyLeaderClosed globalMapCrusadeArmyBuyLeaderClosed)
        {
            RemovePlayerFromTracker(Game.PlayersInGlobalMapCrusadeArmyBuyLeader, globalMapCrusadeArmyBuyLeaderClosed.PlayerId);
            UpdateGlobalMapCrusadeArmyInfoUIStateAfterBuyLeader();

            GlobalMapInteraction.CloseBuyLeaderScreen();
        }

        private void OnNotifyGlobalMapCrusadeArmySetLeaderRecruitClicked(long receivedFrom, NotifyGlobalMapCrusadeArmySetLeaderRecruitClicked globalMapCrusadeArmySetLeaderRecruitClicked)
        {
            GlobalMapInteraction.ClickRecruitmentOnSetLeaderScreen();
        }

        private void OnNotifyGlobalMapCrusadeArmySetLeaderClearClicked(long receivedFrom, NotifyGlobalMapCrusadeArmySetLeaderClearClicked globalMapCrusadeArmyInfoSetLeaderCleared)
        {
            GlobalMapInteraction.ClearLeaderOnCrusadeArmyInfo();
        }

        private void OnNotifyGlobalMapCrusadeArmySetLeaderClosed(long receivedFrom, NotifyGlobalMapCrusadeArmySetLeaderClosed globalMapCrusadeArmyInfoSetLeaderClosed)
        {
            RemovePlayerFromTracker(Game.PlayersInGlobalMapCrusadeArmySetLeader, globalMapCrusadeArmyInfoSetLeaderClosed.PlayerId);

            GlobalMapInteraction.CloseCrusadeArmySetLeaderInfo();

            UpdateGlobalMapCrusadeArmyInfoUIStateAfterSetLeader();
        }

        private void OnNotifyGlobalMapCrusadeArmyInfoCartNameChanged(long receivedFrom, NotifyGlobalMapCrusadeArmyInfoCartNameChanged message)
        {
            var army = Mapper.Map<NetworkGlobalMapArmy>(message.Army);

            GlobalMapInteraction.SetCrusadeArmyInfoCartName(army);
        }

        private void OnNotifyGlobalMapCrusadeArmyMainCartClosed(long receivedFrom, NotifyGlobalMapCrusadeArmyMainCartClosed message)
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmyInfo);

            GlobalMapInteraction.CloseCrusadeArmyMainInfo();
        }

        private void OnNotifyGlobalMapCrusadeArmyCreated(long receivedFrom, NotifyGlobalMapCrusadeArmyCreated message)
        {
            GlobalMapInteraction.CreateCrusadeArmy();

            UpdateGlobalMapRecruitmentUIState();
            UpdateGlobalMapCrusadeArmyInfoUIState();
        }

        private void OnNotifyGlobalMapCrusadeArmyInfoShown(long receivedFrom, NotifyGlobalMapCrusadeArmyInfoShown message)
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapCrusadeArmyInfo, message.PlayerId);

            GlobalMapInteraction.OpenCrusadeArmyInfo();
        }

        private void OnNotifyGlobalMapCrusadeArmiesMerging(long receivedFrom, NotifyGlobalMapCrusadeArmiesMerging message)
        {
            GlobalMapInteraction.OpenCrusadeArmiesMergeScreen();
        }

        private void OnNotifyGlobalMapCrusadeArmyLeaderActionExecuted(long receivedFrom, NotifyGlobalMapCrusadeArmyLeaderActionExecuted message)
        {
            var leader = Mapper.Map<NetworkGlobalMapArmyLeader>(message.Leader);
            var actionType = Mapper.Map<NetworkGlobalMapArmyLeaderActionType>(message.Type);

            GlobalMapInteraction.RunLeaderAction(leader, actionType);
        }

        private void OnNotifyGlobalMapCrusadeArmyInfoPrevMergeArmySelected(long receivedFrom, NotifyGlobalMapCrusadeArmyInfoPrevMergeArmySelected globalMapCrusadeArmyInfoPrevMergeArmySelected)
        {
            GlobalMapInteraction.SelectPrevCrusadeArmyInfoMergeArmy();
        }

        private void OnNotifyGlobalMapCrusadeArmyInfoNextMergeArmySelected(long receivedFrom, NotifyGlobalMapCrusadeArmyInfoNextMergeArmySelected globalMapCrusadeArmyInfoNextMergeArmySelected)
        {
            GlobalMapInteraction.SelectNextCrusadeArmyInfoMergeArmy();
        }

        private void OnNotifyGlobalMapCrusadeArmyMergeCartClosed(long receivedFrom, NotifyGlobalMapCrusadeArmyMergeCartClosed globalMapCrusadeArmyInfoMergeClosed)
        {
            RemovePlayerFromTracker(Game.PlayersInGlobalMapCrusadeArmyInfoMerge, globalMapCrusadeArmyInfoMergeClosed.PlayerId);
            UpdateGlobalMapCrusadeArmyInfoUIStateAfterMerge();

            GlobalMapInteraction.CloseCrusadeArmyMergeInfo();
        }

        private void OnNotifyGlobalMapCrusadeArmySquadsMovedToSecondArmy(long receivedFrom, NotifyGlobalMapCrusadeArmySquadsMovedToSecondArmy crusadeArmySquadsMovedToSecondArmy)
        {
            GlobalMapInteraction.MoveCrusadeArmySquadsToSecondArmy();
        }

        private void OnNotifyGlobalMapCrusadeArmySquadsMovedToMainArmy(long receivedFrom, NotifyGlobalMapCrusadeArmySquadsMovedToMainArmy crusadeArmySquadsMovedToMainArmy)
        {
            GlobalMapInteraction.MoveCrusadeArmySquadsToMainArmy();
        }

        private void OnNotifyGlobalMapCrusadeArmyInfoClosed(long receivedFrom, NotifyGlobalMapCrusadeArmyInfoClosed globalMapCrusadeArmyInfoClosed)
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmyInfo);
            GlobalMapInteraction.CloseCrusadeArmyInfo();
        }

        private void OnNotifyGlobalMapCrusadeArmySquadDismissed(long receivedFrom, NotifyGlobalMapCrusadeArmySquadDismissed crusadeArmySquadDismissed)
        {
            var squadSlot = Mapper.Map<NetworkGlobalMapArmySquadSlot>(crusadeArmySquadDismissed.SquadSlot);

            GlobalMapInteraction.DismissCrusadeArmySquad(squadSlot);
        }

        private void OnNotifyGlobalMapCrusadeArmyMergedInOne(long receivedFrom, NotifyGlobalMapCrusadeArmyMergedInOne crusadeArmyMergedInOne)
        {
            var squadSlot = Mapper.Map<NetworkGlobalMapArmySquadSlot>(crusadeArmyMergedInOne.SquadSlot);

            GlobalMapInteraction.MergeInOneCrusadeArmySquad(squadSlot);
        }

        private void OnNotifyGlobalMapCrusadeArmySquadSplitRequested(long receivedFrom, NotifyGlobalMapCrusadeArmySquadSplitRequested crusadeArmySquadSplitRequested)
        {
            var sourceSquadSlot = Mapper.Map<NetworkGlobalMapArmySquadSlot>(crusadeArmySquadSplitRequested.SourceSquadSlot);
            var targetSquadSlot = Mapper.Map<NetworkGlobalMapArmySquadSlot>(crusadeArmySquadSplitRequested.TargetSquadSlot);

            GlobalMapInteraction.RunSplitRequestForCrusadeArmySquad(sourceSquadSlot, targetSquadSlot, crusadeArmySquadSplitRequested.Count);
        }

        private void OnNotifyGlobalMapCrusadeArmySquadsSwitched(long receivedFrom, NotifyGlobalMapCrusadeArmySquadsSwitched crusadeArmySquadsSwitched)
        {
            var sourceSquadSlot = Mapper.Map<NetworkGlobalMapArmySquadSlot>(crusadeArmySquadsSwitched.SourceSquadSlot);
            var targetSquadSlot = Mapper.Map<NetworkGlobalMapArmySquadSlot>(crusadeArmySquadsSwitched.TargetSquadSlot);

            GlobalMapInteraction.SwitchCrusadeArmySquads(sourceSquadSlot, targetSquadSlot);
        }

        private void OnNotifyGlobalMapCrusadeArmySquadsMerged(long receivedFrom, NotifyGlobalMapCrusadeArmySquadsMerged crusadeArmySquadsMerged)
        {
            var sourceSquadSlot = Mapper.Map<NetworkGlobalMapArmySquadSlot>(crusadeArmySquadsMerged.SourceSquadSlot);
            var targetSquadSlot = Mapper.Map<NetworkGlobalMapArmySquadSlot>(crusadeArmySquadsMerged.TargetSquadSlot);

            GlobalMapInteraction.MergeCrusadeArmySquads(sourceSquadSlot, targetSquadSlot, crusadeArmySquadsMerged.Count);
        }

        private void OnNotifyGlobalMapCrusadeArmySquadSplit(long receivedFrom, NotifyGlobalMapCrusadeArmySquadSplit globalMapCrusadeArmySquadSplit)
        {
            var squadSlot = Mapper.Map<NetworkGlobalMapArmySquadSlot>(globalMapCrusadeArmySquadSplit.SquadSlot);

            GlobalMapInteraction.SplitCrusadeArmySquad(squadSlot, globalMapCrusadeArmySquadSplit.Count);
        }

        private void OnNotifyTacticalCombatAccelerationChanged(long receivedFrom, NotifyTacticalCombatAccelerationChanged message)
        {
            CombatInteraction.SetTacticalCombatAcceleration(message.IsAccelerated);
        }

        private void OnNotifyTacticalCombatRetreated(long receivedFrom, NotifyTacticalCombatRetreated tacticalCombatRetreated)
        {
            CombatInteraction.RetreatFromTacticalCombat();
        }

        private void OnNotifyTacticalCombatTotalDefenseUsed(long receivedFrom, NotifyTacticalCombatTotalDefenseUsed tacticalCombatTotalDefenseUsed)
        {
            CombatInteraction.UseTacticalCombatTotalDefense();
        }

        private void OnNotifyTacticalCombatTurnPostponed(long receivedFrom, NotifyTacticalCombatTurnPostponed tacticalCombatTurnPostponed)
        {
            CombatInteraction.PostponeTacticalCombatTurn();
        }

        private void OnNotifyTacticalUnitMoveToCommandExecuted(long receivedFrom, NotifyTacticalUnitMoveToCommandExecuted tacticalUnitMoveToCommandExecuted)
        {
            var command = Mapper.Map<NetworkTacticalUnitMoveToCommand>(tacticalUnitMoveToCommandExecuted.Command);

            CombatInteraction.RunTacticalUnitMoveToCommand(command);
        }

        private void OnNotifyTacticalUnitUseAbilityCommandExecuted(long receivedFrom, NotifyTacticalUnitUseAbilityCommandExecuted tacticalUnitUseAbilityCommandExecuted)
        {
            var command = Mapper.Map<NetworkTacticalUnitUseAbilityCommand>(tacticalUnitUseAbilityCommandExecuted.Command);

            CombatInteraction.RunTacticalUnitUseAbilityCommand(command);
        }

        private async void OnNotifyTacticalUnitAttackCommandExecuted(long receivedFrom, NotifyTacticalUnitAttackCommandExecuted message)
        {
            var command = Mapper.Map<NetworkTacticalUnitAttackCommand>(message.Command);

            await WaitWhileTrue(() => Game.ArmyCombat == null || !string.Equals(Game.ArmyCombat.Turn.UnitId, message.Command.UnitId, StringComparison.OrdinalIgnoreCase),
                "Waiting for unit turn to start");

            CombatInteraction.RunTacticalUnitAttackCommand(command);
        }

        private void OnNotifyGlobalMapCombatResultsClosed(long receivedFrom, NotifyGlobalMapCombatResultsClosed globalMapCombatResultsClosed)
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapCombatResults);
            GlobalMapInteraction.CloseCombatResults();
        }

        private void OnNotifyCrusadeArmyBattleResultsClosed(long receivedFrom, NotifyCrusadeArmyBattleResultsClosed crusadeArmyBattleResultsClosed)
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmyBattleResults);
            GlobalMapInteraction.CloseCrusadeArmyBattleResults();
        }

        private void OnNotifyCrusadeArmyBattleResultsManualCombatStarted(long receivedFrom, NotifyCrusadeArmyBattleResultsManualCombatStarted crusadeArmyBattleResultsManualCombatStarted)
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmyBattleResults);

            GlobalMapInteraction.StartCrusadeArmyBattleResultsManualCombat();
        }

        private async void OnNotifyTacticalCombatInitialized(long receivedFrom, NotifyTacticalCombatInitialized tacticalCombatInitialized)
        {
            await WaitWhileTrue(() => Game.ArmyCombat == null || !CombatInteraction.IsInCrusadeTacticalCombat(), "Crusade army combat has not been started yet");

            Game.ArmyCombat.AreaSeed = tacticalCombatInitialized.AreaSeed;
            Game.ArmyCombat.Seed = tacticalCombatInitialized.Seed;
            CombatInteraction.InitializeCrusadeArmyCombat();
        }

        private void OnNotifyGlobalMapAutoCrusadeCombatChanged(long receivedFrom, NotifyGlobalMapAutoCrusadeCombatChanged globalMapAutoCrusadeCombatChanged)
        {
            GlobalMapInteraction.SetAutoCrusadeCombat(globalMapAutoCrusadeCombatChanged.IsEnabled);
        }

        private void OnNotifyGlobalMapSelectedArmyChanged(long receivedFrom, NotifyGlobalMapSelectedArmyChanged globalMapSelectedArmyChanged)
        {
            var army = Mapper.Map<NetworkGlobalMapArmy>(globalMapSelectedArmyChanged.Army);

            GlobalMapInteraction.SetSelectedArmy(army);
        }

        private void OnNotifyGlobalMapDaySkipped(long receivedFrom, NotifyGlobalMapDaySkipped globalMapDaySkipped)
        {
            GlobalMapInteraction.SkipDay();
        }

        private void OnNotifyGlobalMapCommonPopupDeclined(long receivedFrom, NotifyGlobalMapCommonPopupDeclined message)
        {
            RemovePlayerFromTracker(Game.PlayersInGlobalMapCommonPopup, message.PlayerId);

            GlobalMapInteraction.DeclineCommonPopup();
        }

        private void OnNotifyGlobalMapLocationMessageAccepted(long playerId, NotifyGlobalMapLocationMessageAccepted message)
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapLocationMessage);

            GlobalMapInteraction.AcceptLocationMessageBox();
        }

        private void OnNotifyGlobalMapLocationMessageClosed(long playerId, NotifyGlobalMapLocationMessageClosed globalMapLocationMessageClosed)
        {
            RemovePlayerFromTracker(Game.PlayersInGlobalMapLocationMessage, globalMapLocationMessageClosed.PlayerId);

            GlobalMapInteraction.CloseLocationMessageBox();
        }

        private void OnNotifyPolymorphicItemCreated(long playerId, NotifyPolymorphicItemCreated polymorphicItemCreated)
        {
            var polymorphicItem = Mapper.Map<NetworkPolymorphicItem>(polymorphicItemCreated.PolymorphicItem);
            GameInteraction.CreateAndEquipPolymorphicItem(polymorphicItem, createContext: true);
        }

        private void OnNotifyNewGameSequenceTerminated(long playerId, NotifyNewGameSequenceTerminated newGameSequenceTerminated)
        {
            GameInteraction.TerminateNewGameSequence();
        }

        private void OnNotifyNewGameSequenceLevelingStarted(long playerId, NotifyNewGameSequenceLevelingStarted newGameSequenceLevelingStarted)
        {
            GameInteraction.StartNewGameSequenceLeveling();
        }

        private void OnNotifyNewGameSequencePhaseChanged(long playerId, NotifyNewGameSequencePhaseChanged newGameSequencePhaseChanged)
        {
            var phase = Mapper.Map<NetworkNewGameSequencePhase>(newGameSequencePhaseChanged.Phase);

            GameInteraction.SelectNewGameSequencePhase(phase);
        }

        private void OnNotifyNewGameDifficultyChanged(long playerId, NotifyNewGameDifficultyChanged newGameDifficultyChanged)
        {
            GameInteraction.SelectNewGameDifficulty(newGameDifficultyChanged.Difficulty);
        }

        private void OnNotifyCharacterSelectionWindowClosed(long playerId, NotifyCharacterSelectionWindowClosed characterSelectionWindowClosed)
        {
            GameInteraction.CloseCharacterSelectionWindow();
        }

        private void OnNotifyCharacterSelectionWindowAccepted(long playerId, NotifyCharacterSelectionWindowAccepted characterSelectionWindowAccepted)
        {
            GameInteraction.AcceptCharacterSelectionWindow();
        }

        private void OnNotifyCharacterSelectionToggleChanged(long playerId, NotifyCharacterSelectionToggleChanged characterSelectionToggleChanged)
        {
            GameInteraction.ToggleCharacterSelectionWindow(characterSelectionToggleChanged.UnitId);
        }

        private void OnNotifyZoneLootRemoveToggleChanged(long receivedFrom, NotifyZoneLootRemoveToggleChanged zoneLootRemoveToggleChanged)
        {
            GameInteraction.UpdateZoneLootRemoveToggle(zoneLootRemoveToggleChanged.RemoveLoot);
        }

        private void OnNotifyZoneLootLeft(long receivedFrom, NotifyZoneLootLeft zoneLootLeft)
        {
            GameInteraction.LeaveZoneLoot();
        }

        private void OnNotifyZoneLootCompleted(long receivedFrom, NotifyZoneLootCompleted zoneLootCompleted)
        {
            GameInteraction.CompleteZoneLoot();
        }

        private void OnNotifyGlobalMapEncounterRolled(long receivedFrom, NotifyGlobalMapEncounterRolled message)
        {
            var encounter = Mapper.Map<NetworkGlobalMapEncounter>(message.Encounter);

            GlobalMapInteraction.RollEncounter(encounter);
        }

        private void OnNotifyGlobalMapEncounterAvoided(long receivedFrom, NotifyGlobalMapEncounterAvoided globalMapEncounterAvoided)
        {
            GlobalMapInteraction.AvoidEncounter();

            ResetPlayersTracker(Game.PlayersInGlobalMapEncounterMessage);
        }

        private void OnNotifyGlobalMapEncounterAccepted(long receivedFrom, NotifyGlobalMapEncounterAccepted notifyGlobalMapEncounterAccepted)
        {
            GlobalMapInteraction.AcceptEncounter();

            ResetPlayersTracker(Game.PlayersInGlobalMapEncounterMessage);
        }

        private void OnNotifyGlobalMapIngredientCollectionAccepted(long receivedFrom, NotifyGlobalMapCommonPopupAccepted globalMapCommonPopupAccepted)
        {
            var popup = Mapper.Map<NetworkGlobalMapCommonPopup>(globalMapCommonPopupAccepted.Popup);
            GlobalMapInteraction.AcceptCommonPopup(popup);

            ResetPlayersTracker(Game.PlayersInGlobalMapCommonPopup);
        }

        private void OnNotifyGlobalMapTravelContinued(long receivedFrom, NotifyGlobalMapTravelContinued globalMapTravelContinued)
        {
            var traveler = Mapper.Map<NetworkGlobalMapTraveler>(globalMapTravelContinued.Traveler);
            GlobalMapInteraction.ContinueTravel(traveler);
        }

        private void OnNotifyGlobalMapTravelStopped(long receivedFrom, NotifyGlobalMapTravelStopped globalMapTravelStopped)
        {
            var traveler = Mapper.Map<NetworkGlobalMapTraveler>(globalMapTravelStopped.Traveler);
            GlobalMapInteraction.StopTravel(traveler);
        }

        private void OnNotifySkipTimeStarted(long receivedFrom, NotifySkipTimeStarted skipTimeStarted)
        {
            ResetPlayersTracker(Game.PlayersInSkipTime);
            GameInteraction.StartSkipTime();
        }

        private void OnNotifySkipTimeHoursChanged(long receivedFrom, NotifySkipTimeHoursChanged skipTimeHoursChanged)
        {
            GameInteraction.UpdateSkipTimeHours(skipTimeHoursChanged.Hours);
        }

        private void OnNotifySkipTimeClosed(long receivedFrom, NotifySkipTimeClosed skipTimeClosed)
        {
            GameInteraction.CloseSkipTimeUI();
            ResetPlayersTracker(Game.PlayersInSkipTime);
        }

        private void OnNotifyGlobalMapTravelStarted(long receivedFrom, NotifyGlobalMapTravelStarted globalMapTravelStarted)
        {
            var travel = Mapper.Map<NetworkGlobalMapTravel>(globalMapTravelStarted.Travel);

            GlobalMapInteraction.StartTravel(travel);
        }

        private void OnNotifyGlobalMapRestOpened(long receivedFrom, NotifyGlobalMapRestOpened message)
        {
            GameInteraction.InitiateRest();
        }

        private void OnNotifyGlobalMapGroupChangerOpened(long receivedFrom, NotifyGlobalMapGroupChangerOpened message)
        {
            GlobalMapInteraction.OpenGroupChanger();
        }

        private void OnNotifyRestWindowClosed(long receivedFrom, NotifyRestWindowClosed message)
        {
            GameInteraction.CloseRestWindow();
        }

        private void OnNotifyGroupChangerPartyAccepted(long playerId, NotifyGroupChangerPartyAccepted groupChangerPartyAccepted)
        {
            GameInteraction.AcceptGroupChangerParty();
            ResetPlayersTracker(Game.PlayersInGroupChanger);
        }

        private void OnNotifyGroupChangerUnitClicked(long playerId, NotifyGroupChangerUnitClicked groupChangerUnitClicked)
        {
            GameInteraction.ClickGroupChangerUnit(groupChangerUnitClicked.UnitId);
        }

        private void OnNotifyGroupChangerClosed(long playerId, NotifyGroupChangerClosed groupChangerClosed)
        {
            GameInteraction.CloseGroupChangerUI();
            ResetPlayersTracker(Game.PlayersInGroupChanger);
        }

        private void OnNotifyLobbySyncStatusChanged(long receivedFrom, NotifySaveGameSyncStatusChanged lobbySyncStatusChanged)
        {
            var status = Mapper.Map<NetworkLobbySyncStatus>(lobbySyncStatusChanged.Status);
            UpdateLobbySyncStatus(lobbySyncStatusChanged.PlayerId, status);
        }

        private void OnNotifyLevelingStarted(long playerId, NotifyLevelingStarted characterLevelingStarted)
        {
            if (!Enum.TryParse<NetworkLevelingType>(characterLevelingStarted.Type, true, out var levelingType))
            {
                Logger.LogError("Invalid leveling type value. Value={Value}", characterLevelingStarted.Type);
                return;
            }

            InitiateLeveling(characterLevelingStarted.UnitId, levelingType);
            LevelingInteraction.StartLeveling(Game.Leveling.UnitId, Game.Leveling.Type);
        }

        private void OnNotifyVendorWindowClosed(long playerId, NotifyVendorWindowClosed closed)
        {
            GameInteraction.CloseVendorWindow();
        }

        private void OnNotifyVendorDealMade(long playerId, NotifyVendorDealMade made)
        {
            GameInteraction.MakeVendorDeal();
        }

        private void OnNotifyInvalidCombatTurnStarted(long playerId, NotifyInvalidCombatTurnStarted message)
        {
            PlayerNotification.AddCombatText(WellKnownKeys.GameNotifications.Combat.Turn.ClientOrderDesync.Key, CombatTextSeverity.Debug, new UnitLogParameter(message.UnitId));
            ResetCombatTurn();
            CombatInteraction.StartTurnBasedCombatTurn(message.UnitId, () =>
            {
                var invalidTurnMessage = new ClientInvalidUnitTurnStartRequested
                {
                    UnitId = message.UnitId
                };
                Send(invalidTurnMessage);
            });
        }

        private void OnNotifyRestStarted(long playerId, NotifyRestStarted started)
        {
            GameInteraction.StartRest();
        }

        private void OnNotifyCampingUnitsRoleChanged(long playerId, NotifyCampingUnitsRoleChanged changed)
        {
            var roles = Mapper.Map<List<NetworkCampingRole>>(changed.Roles);
            GameInteraction.SetCampingRoles(roles);
        }

        private void OnNotifyCampingStateChanged(long playerId, NotifyCampingStateChanged changed)
        {
            var state = Mapper.Map<NetworkCampingState>(changed.State);
            GameInteraction.SetCampingState(state);
        }

        private void OnNotifyCampingUseHealingSpellsChanged(long playerId, NotifyCampingUseHealingSpellsChanged changed)
        {
            GameInteraction.SetCampingUseHealingSpells(changed.IsOn);
        }

        private void OnNotifySpawnCampPlace(long playerId, NotifySpawnCampPlace place)
        {
            var position = Mapper.Map<NetworkVector3>(place.Position);
            GameInteraction.SpawnCampPlace(position);
        }

        private void OnNotifyInspectionKnowledgeCheckRolled(long playerId, NotifyInspectionKnowledgeCheckRolled rolled)
        {
            var check = Mapper.Map<NetworkInspectionKnowledgeCheck>(rolled.Check);
            GameInteraction.ApplyInspectionKnowledgeCheck(check);
        }

        private void OnNotifyPerceptionCheckRolled(long playerId, NotifyPerceptionCheckRolled rolled)
        {
            var check = Mapper.Map<NetworkPerceptionCheck>(rolled.Check);
            GameInteraction.ApplyPerceptionCheck(check);
        }

        private void OnNotifyStealthPerceptionCheckRolled(long playerId, NotifyStealthPerceptionCheckRolled rolled)
        {
            var check = Mapper.Map<NetworkStealthPerceptionCheck>(rolled.Check);
            GameInteraction.ApplyStealthPerceptionCheck(check);
        }

        private async void OnNotifyCombatTurnStartSynchronizationRequired(long playerId, NotifyCombatTurnStartSynchronizationRequired message)
        {
            try
            {
                await WaitWhileTrue(() => Game.Combat?.Turn == null, "Turn has not been initialized yet");

                var combatState = Mapper.Map<NetworkCombatState>(message.CombatState);
                var areaEffects = Mapper.Map<List<NetworkAreaEffect>>(message.TriggeredAreaEffects);
                Game.Combat.TriggeredAreaEffects.AddRange(areaEffects);

                await CombatInteraction.UpdateCombatStateAsync(combatState, areaEffects, false);

                ValueGenerator.ResetSeededGenerators(Random.IdentifierLifetime.CombatTurn);

                var confirmationMessage = new ClientCombatTurnStartSynchronized { UnitId = Game.Combat.Turn.UnitId };
                Send(confirmationMessage);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to sync combat turn");
                throw;
            }
        }

        private async void OnNotifyCombatTurnStarted(long playerId, NotifyCombatTurnStarted message)
        {
            if (Game.Combat?.Turn == null)
            {
                Logger.LogError("Trying to start not initialized turn. Round={Round}, UnitId={UnitId}", message.Round, message.UnitId);
                return;
            }

            if (!string.Equals(message.UnitId, Game.Combat.Turn.UnitId, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning("Starting turn with different UnitId. LocalUnitId={LocalUnitId}, HostUnitId={HostUnitId}", Game.Combat.Turn.UnitId, message.UnitId);
            }

            if (Game.Combat.Round != message.Round)
            {
                Logger.LogWarning("Starting turn with different Round number. LocalRound={LocalRound}, HostRound={HostRound}", Game.Combat.Round, message.Round);
            }

            Game.Combat.Turn.Seed = message.Seed;

            var delay = GetTurnStartDelay();
            Logger.LogInformation("Starting combat turn. Delay={Delay}, UnitId={UnitId}, IsAI={IsAI}, TurnSeed={TurnSeed}", delay, Game.Combat.Turn.UnitId, Game.Combat.Turn.IsAI, Game.Combat.Turn.Seed);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            SetCombatTurnStage(NetworkCombatTurnStage.Playing);
            CombatInteraction.StartTurnBasedCombatTurn(Game.Combat.Turn.UnitId, () =>
            {
                OnTurnEnded(message.UnitId);
                Game.Combat.Turn = null;
                Logger.LogWarning("Turn has been reset because the turn could not be started. UnitId={UnitId}", message.UnitId);
            });
        }

        private async void OnNotifyCombatInitializationRequired(long playerId, NotifyCombatInitializationRequired message)
        {
            await WaitWhileTrue(() => Game.Combat == null || Game.CurrentArea == null || Game.CurrentArea.Seed == 0, "Combat has not been started on client yet. Waiting until start");

            Game.Combat.Seed = message.CombatSeed;
            Logger.LogInformation("Combat seed has been configured. Seed={Seed}", Game.Combat.Seed);

            var combatState = Mapper.Map<NetworkCombatState>(message.State);
            var areaEffects = Mapper.Map<List<NetworkAreaEffect>>(message.TriggeredAreaEffects);
            Game.Combat.TriggeredAreaEffects.AddRange(areaEffects);

            await CombatInteraction.UpdateCombatStateAsync(combatState, areaEffects, true);

            var confirmation = new ClientCombatInitializationCompleted();
            Send(confirmation);
        }

        private async void OnNotifyDialogStarted(long playerId, NotifyDialogStarted started)
        {
            await WaitWhileTrue(() => Game.DialogState != null && Game.DialogState.IsSelectingAnswer, "Waiting until the previous answer has been processed");

            var dialog = Mapper.Map<NetworkDialog>(started.Dialog);

            if (Game.DialogState?.Dialog == null || !string.Equals(Game.DialogState.Dialog.Id, started.Dialog.Id, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogInformation("New dialog has been initiated. PreviousDialogName={PreviousDialogName}, CurrentDialogName={CurrentDialogName}", Game.DialogState?.Dialog.Name, started.Dialog.Name);
                Game.DialogState = new NetworkDialogState(dialog);
            }

            var hasStartedDialog = await DialogInteraction.StartDialogAsync(dialog);
            if (!hasStartedDialog)
            {
                Logger.LogWarning("Client dialog is already started. Id={Id}, Name={Name}", dialog.Id, dialog.Name);
            }
        }

        private void OnNotifyDialogPopupAccepted(long playerId, NotifyDialogPopupAccepted message)
        {
            var popup = Mapper.Map<NetworkDialogPopup>(message.Popup);

            DialogInteraction.AcceptDialogPopup(popup);
        }

        private void OnNotifyDialogPopupClosed(long playerId, NotifyDialogPopupClosed message)
        {
            var popup = Mapper.Map<NetworkDialogPopup>(message.Popup);

            DialogInteraction.CloseDialogPopup(popup);
        }

        private void OnNotifyDialogCueAnswerSelected(long playerId, NotifyDialogCueAnswerSelected message)
        {
            Game.DialogState.Answer = new NetworkDialogAnswer
            {
                AnswerName = message.AnswerName,
                CueName = message.CueName,
                ManualUnitSelectionId = message.ManualUnitSelectionId,
            };

            Game.DialogState.AnswerSuggestions.Clear();
            DialogInteraction.SelectDialogAnswer(message.AnswerName, message.ManualUnitSelectionId);
        }

        private async void OnNotifyDialogCueAnswerSuggested(long playerId, NotifyDialogCueAnswerSuggested message)
        {
            await WaitWhileTrue(() => Game.DialogState == null || !string.Equals(Game.DialogState.CurrentCueName, message.CueName, StringComparison.OrdinalIgnoreCase), "Waiting for dialog to initialize before suggesting cue answer");

            var suggestions = Mapper.Map<List<NetworkDialogAnswerSuggestion>>(message.Suggestions);
            var players = GetSyncedPlayers();
            DialogInteraction.MarkSuggestedCueAnswers(players, suggestions);
        }

        private void OnNotifyPartyAreaTransitioned(long playerId, NotifyPartyAreaTransitioned partyLeftArea)
        {
            // make sure next area load is not affected by last combat turn seed
            ResetLastCombatTurn();

            var transition = Mapper.Map<NetworkAreaTransition>(partyLeftArea.Transition);
            GameInteraction.LeaveArea(transition);
        }

        private void OnNotifyGamePauseEnded(long playerId, NotifyGamePauseEnded message)
        {
            if (Game.ForcedPause != null && Game.ForcedPause.Reason == NetworkForcedPauseReason.AreaLoading)
            {
                GlobalMapInteraction.EnableInteractions();
            }

            Game.ForcedPause = null;
            GameInteraction.SetPause(false);
        }

        private void OnNotifyGameStarted(long playerId, NotifyGameStarted started)
        {
            OnGameStarted?.Invoke();

            if (Game.StartUp.IsNewGameSequence)
            {
                StartNewGameSequence();
                return;
            }

            if (string.IsNullOrEmpty(Game.StartUp?.SavePath))
            {
                Logger.LogCritical("Trying to start a game with missing save file path");
                return;
            }

            LoadSavedGame();
        }

        private void OnNotifyCharacterOwnerChanged(long playerId, NotifyCharacterOwnerChanged characterOwnerChanged)
        {
            try
            {
                var newOwner = GetPlayer(characterOwnerChanged.Character.OwnerId);

                var character = Mapper.Map<NetworkCharacter>(characterOwnerChanged.Character);
                var actualCharacter = FindCharacter(character);
                if (actualCharacter == null)
                {
                    Logger.LogError("Unable to find character. CharacterName={CharacterName}, CharacterId={CharacterId}", character.Name, character.UnitId);
                    return;
                }
                actualCharacter.Owner = newOwner;
                if (!string.IsNullOrEmpty(character.UnitId))
                {
                    UpdateCharacterOwnershipHistory(character);
                }

                OnCharacterOwnerChanged?.Invoke(actualCharacter);

                UpdateInGameCharacterOwnershipChange(actualCharacter);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to handle changed character ownership");
                throw;
            }
        }

        private void OnNotifyLobbyCharactersChanged(long playerId, NotifyLobbyCharactersChanged lobbyCharactersChanged)
        {
            Game.Characters.Clear();
            ResetCharacterOwnership();
            foreach (var networkCharacter in lobbyCharactersChanged.Characters)
            {
                var character = Mapper.Map<NetworkCharacter>(networkCharacter);
                character.Owner = GetPlayer(networkCharacter.OwnerId);
                Game.Characters.Add(character);
            }

            OnCharactersChanged?.Invoke(lobbyCharactersChanged.Title, Game.Characters);
        }

        private void OnNotifyLobbyPlayersChanged(long playerId, NotifyLobbyPlayersChanged playersChanged)
        {
            // a lot of lame lookups below, but shouldn't really matter for a small collection size
            var disconnectedPlayers = Game.Players.Where(x => !playersChanged.Players.Any(c => c.Id == x.Id)).ToList();
            var newPlayers = playersChanged.Players.Where(x => !Game.Players.Any(c => c.Id == x.Id)).ToList();
            // no need to handle player info updates here as any ready/loading/etc statuses are synced separately

            foreach (var disconnectedPlayer in disconnectedPlayers)
            {
                CleanupPlayer(disconnectedPlayer);
                ShowPlayerDisconnectedMessage(disconnectedPlayer);

                RefreshUIOnPlayerDisconnect(disconnectedPlayer.Id);
            }

            foreach (var newPlayer in newPlayers)
            {
                var player = Mapper.Map<NetworkPlayer>(newPlayer);
                Game.Players.Add(player);
                ShowPlayerConnectedMessage(player);
            }

            InvokeOnPlayersChanged();
        }

        private void OnNetworkClientConnected(EndPoint endpoint, string gameCode)
        {
            Game = new NetworkGame(null)
            {
                Connectivity = new GameConnectivity
                {
                    Endpoint = endpoint,
                    External = string.IsNullOrEmpty(gameCode) ? null : new ExternalConnectivity
                    {
                        Code = gameCode,
                        Status = ExternalConnectivityStatus.Connected
                    }
                }
            };

            OnConnected?.Invoke(Game.Connectivity);
        }

        private void OnNetworkClientError(NetworkError networkError)
        {
            switch (networkError.Type)
            {
                case NetworkErrorType.SocketError when !string.IsNullOrEmpty(networkError.Reason):
                    InvokeOnNetworkError(WellKnownKeys.MultiplayerClient.Errors.NetworkError.Key, networkError.Reason);
                    break;
                case NetworkErrorType.Disconnected:
                    if (Game?.RealTimeCombat != null
                        && (Game.RealTimeCombat.BlocksSaving
                            || Game.Combat?.Stage == NetworkCombatStage.Running
                                && Game.RealTimeCombat.Mode == CombatMode.RealTimeWithPause))
                    {
                        _disconnectedDuringRealTimeCombat = true;
                        Logger.LogWarning("Disconnected during real-time combat. Saving remains blocked until this game session is closed.");
                    }
                    if (Game?.Players != null)
                    {
                        // TODO: make a proper cleanup to unblock all possible UI elements as client is in single-player now
                        Game.Players.Clear();
                        GameInteraction.MakeModalMessageInteractable();
                        UpdateRespecWindowStateOnPlayerLeave(Game.LocalPlayerId);
                    }
                    InvokeOnNetworkError(WellKnownKeys.MultiplayerClient.Errors.Disconnected.Key, networkError.Reason);
                    break;
                case NetworkErrorType.UnreachableSignalingServer:
                    InvokeOnNetworkError(WellKnownKeys.MultiplayerWindow.ExternalServers.Errors.UnreachableSignalingServer.Key, networkError.Reason);
                    break;
                case NetworkErrorType.GameHostUnavailable:
                    InvokeOnNetworkError(WellKnownKeys.MultiplayerClient.Errors.UnreachableGameHost.Key);
                    break;
                case NetworkErrorType.GameNotFound:
                    InvokeOnNetworkError(WellKnownKeys.MultiplayerClient.Errors.GameNotFound.Key);
                    break;
                case NetworkErrorType.P2PTimeout:
                    InvokeOnNetworkError(WellKnownKeys.MultiplayerClient.Errors.PeerToPeerTimeout.Key);
                    break;
                case NetworkErrorType.ModConflict:
                    InvokeOnNetworkError(WellKnownKeys.MultiplayerWindow.ExternalServers.Errors.ModConflict.Key);
                    break;
                case NetworkErrorType.Generic:
                default:
                    InvokeOnNetworkError(WellKnownKeys.MultiplayerClient.Errors.GenericError.Key);
                    break;
            }
        }

        private void OnGameServerConnectionSucceeded(long playerId, GameServerConnectionSucceeded connectionSucceeded)
        {
            Game.LocalPlayerId = connectionSucceeded.ClientPlayerId;
            Game.SessionSeed = connectionSucceeded.SessionSeed;

            var settings = Mapper.Map<NetworkGameSettings>(connectionSucceeded.GameSettings);
            GameInteraction.ApplyGameSettings(settings);

            var contentState = GetInstalledContent();
            var message = new ClientGameServerConnectionConfirmed
            {
                PlayerName = SettingsService.GetSettings().PlayerName,
                ContentState = Mapper.Map<Networking.Messages.Contracts.NetworkContentState>(contentState)
            };
            Send(message);
        }

        private void InvokeOnNetworkError(string error, string reason = null)
        {
            OnNetworkError?.Invoke();
            PlayerNotification.ShowModalMessage(error, args: reason);
        }

        private TimeSpan GetTurnStartDelay()
        {
            if (!Game.Combat.Turn.IsAI)
            {
                return TimeSpan.Zero;
            }

            var settings = SettingsService.GetSettings();
            return settings.CombatTurnDelayForAI;
        }
    }
}
