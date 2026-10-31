using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Kingmaker.Controllers.Rest;
using Kingmaker.GameModes;
using Kingmaker.UI.Kingdom;
using Kingmaker.Utility;
using Microsoft.Extensions.Logging;
using UniRx;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Abstractions.GameInteraction.CombatLog;
using WOTRMultiplayer.Abstractions.IO;
using WOTRMultiplayer.Abstractions.Random;
using WOTRMultiplayer.Abstractions.Settings;
using WOTRMultiplayer.Abstractions.Unity;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.ActionBar;
using WOTRMultiplayer.Entities.Area;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.Crusades;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Connectivity;
using WOTRMultiplayer.Entities.Content;
using WOTRMultiplayer.Entities.Dialogs;
using WOTRMultiplayer.Entities.Equipment;
using WOTRMultiplayer.Entities.GlobalMap;
using WOTRMultiplayer.Entities.GlobalMap.Kingdom;
using WOTRMultiplayer.Entities.Items;
using WOTRMultiplayer.Entities.Leveling;
using WOTRMultiplayer.Entities.MapObjects;
using WOTRMultiplayer.Entities.NewGame;
using WOTRMultiplayer.Entities.Ping;
using WOTRMultiplayer.Entities.Rest;
using WOTRMultiplayer.Entities.Settings;
using WOTRMultiplayer.Entities.SpellbookManagement;
using WOTRMultiplayer.Entities.Spells;
using WOTRMultiplayer.Entities.Vendor;
using WOTRMultiplayer.Networking.Abstractions;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Networking.Messages.Lobby;
using WOTRMultiplayer.Services.Random;

namespace WOTRMultiplayer.Services
{
    public abstract partial class MultiplayerActorBase
    {
        public const int SaveSizeLimit = 500 * 1024 * 1024;

        private readonly object _actionLock = new();

        private readonly INetworkConnection _networkConnection;

        private SeedKind[] AllSeeds { get; } = [.. Enum.GetValues(typeof(SeedKind)).Cast<SeedKind>().Where(s => s != SeedKind.All)];

        private DateTime? _pauseCooldown;

        private long _nextRealTimeUnitDeathSequence;

        private readonly Dictionary<string, DeferredLevelingSession> _deferredLevelings = new(StringComparer.OrdinalIgnoreCase);
        private DeferredLevelingSession _activeDeferredLeveling;
        private bool _replayingDeferredLeveling;
        private long _levelingDispatchGeneration;
        private NetworkGame _levelingFailureSession;
        private int? _levelingFailureSaveSeed;

        private sealed class DeferredLevelingSession
        {
            public NetworkLevelingIdentity Identity { get; set; }
            public NetworkGame Session { get; set; }
            public long OwnerId { get; set; }
            public string UnitId { get; set; }
            public NetworkLevelingType Type { get; set; }
            public Queue<(object Message, Action Apply)> Messages { get; } = new();
            public bool HasTerminalMessage { get; set; }
            public bool AwaitingForcedUI { get; set; }
            public bool AwaitingOwnedUI { get; set; }
            public bool OwnedStartScheduled { get; set; }
            public bool Failed { get; set; }
            public bool TerminalApplied { get; set; }
        }

        protected bool HasLevelingSynchronizationFailure
        {
            get
            {
                var session = Game;
                return session != null && ReferenceEquals(session, _levelingFailureSession)
                    && session.LoadedSaveSeed == _levelingFailureSaveSeed;
            }
        }

        protected bool IsSavingBlockedByLevelingSync
        {
            get
            {
                lock (ActionLock)
                    return Game != null && (HasLevelingSynchronizationFailure || Game.Leveling != null
                        || _deferredLevelings.Values.Any(value => ReferenceEquals(value.Session, Game)
                            && IsCurrentLevelingIdentity(value.Identity)));
            }
        }

        protected bool IsLevelingOwnershipLocked(string unitId)
        {
            lock (ActionLock)
            {
                if (Game == null) return false;
                if (HasLevelingSynchronizationFailure) return true;
                if (string.IsNullOrEmpty(unitId)) return false;
                bool Affects(string progressingUnit) => !string.IsNullOrEmpty(progressingUnit)
                    && (string.Equals(progressingUnit, unitId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(GameInteraction.GetPetOwnerId(progressingUnit), unitId, StringComparison.OrdinalIgnoreCase));
                return Affects(Game.Leveling?.UnitId)
                    || _deferredLevelings.Values.Any(value => ReferenceEquals(value.Session, Game)
                        && IsCurrentLevelingIdentity(value.Identity) && !value.TerminalApplied && Affects(value.UnitId));
            }
        }

        private void FailLevelingSynchronization(NetworkGame session, NetworkLevelingIdentity identity, Exception error)
        {
            lock (ActionLock)
            {
                if (!ReferenceEquals(Game, session) || !IsCurrentLevelingIdentity(identity)
                    || HasLevelingSynchronizationFailure) return;
                _levelingFailureSession = session;
                _levelingFailureSaveSeed = identity.LoadedSaveSeed;
            }
            Logger.LogError(error, "Leveling synchronization failed; saving remains blocked. UnitId={UnitId}", identity.UnitId);
            MainThreadAccessor.Post(() =>
            {
                if (!ReferenceEquals(Game, session) || !HasLevelingSynchronizationFailure) return;
                GameInteraction.SetPause(true);
                PlayerNotification.ShowWarningNotification(WellKnownKeys.GameNotifications.Leveling.SynchronizationFailed.Key);
            });
        }

        public NetworkArea CurrentArea => Game.CurrentArea;

        public bool IsInCombat => Game?.Combat != null;

        public int SessionSeed => Game.SessionSeed;

        public int? LoadedSaveSeed => Game.LoadedSaveSeed;

        public int? AreaSeed => Game.CurrentArea?.Seed;

        public int? CombatSeed => Game.Combat?.Seed;

        public int? CombatTurnSeed => Game.Combat?.Turn?.Seed ?? Game.LastCombatTurn?.Seed;

        public int? CrusadeArmyCombatAreaSeed => Game.ArmyCombat?.AreaSeed;

        public int? CrusadeArmyCombatSeed => Game.ArmyCombat?.Seed;

        public Action<NetworkLobbyStage, List<NetworkPlayer>> OnPlayersChanged { get; set; }

        public Action<string, List<NetworkCharacter>> OnCharactersChanged { get; set; }

        public Action<bool> OnNewGameSequenceStarted { get; set; }

        public Action OnGameStarted { get; set; }

        public Action<Dictionary<long, float>> OnSaveGameTransferProgressChanged { get; set; }

        public Action<GameConnectivity> OnConnected { get; set; }

        public Action<GameConnectivity> OnGameConnectivityUpdated { get; set; }

        internal NetworkGame Game { get; set; }

        protected ILogger Logger { get; private set; }
        protected virtual IMainThreadAccessor MainThreadAccessor => null;

        // Async message handlers must never throw: an exception escaping an async void
        // handler is rethrown on the Unity synchronization context and kills the game.
        // Handlers run as SafeFireAndForget wrappers so a handler failure is logged
        // and the message pump keeps running.
        protected async void SafeFireAndForget(Func<Task> action, [System.Runtime.CompilerServices.CallerMemberName] string handler = null)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unhandled error in async message handler. Handler={Handler}", handler);
            }
        }

        protected IMapper Mapper { get; private set; }

        protected IGameInteractionService GameInteraction { get; private set; }

        protected ILevelingInteractionService LevelingInteraction { get; private set; }

        protected IPlayerNotificationService PlayerNotification { get; private set; }

        protected IDialogInteractionService DialogInteraction { get; private set; }

        protected IGlobalMapInteractionService GlobalMapInteraction { get; private set; }

        protected IPingInteractionService PingInteraction { get; private set; }

        protected ICombatInteractionService CombatInteraction { get; private set; }

        protected IFileSystemService FileSystem { get; private set; }

        protected IMultiplayerSettingsService SettingsService { get; private set; }

        protected IValueGenerator ValueGenerator { get; private set; }

        protected abstract bool HasControlOverUI { get; }

        protected object ActionLock => _actionLock;

        protected MultiplayerActorBase(
            ILogger logger,
            IMapper mapper,
            IMultiplayerSettingsService multiplayerSettingsService,
            IGameInteractionService gameInteractionService,
            ILevelingInteractionService levelingInteractionService,
            IPlayerNotificationService playerNotificationService,
            IDialogInteractionService dialogInteractionService,
            IGlobalMapInteractionService globalMapInteractionService,
            IPingInteractionService pingInteractionService,
            ICombatInteractionService combatInteractionService,
            IFileSystemService fileSystemService,
            IValueGenerator valueGenerator,
            INetworkConnection networkConnection)
        {
            Logger = logger;
            Mapper = mapper;
            GameInteraction = gameInteractionService;
            LevelingInteraction = levelingInteractionService;
            PlayerNotification = playerNotificationService;
            DialogInteraction = dialogInteractionService;
            GlobalMapInteraction = globalMapInteractionService;
            PingInteraction = pingInteractionService;
            CombatInteraction = combatInteractionService;
            FileSystem = fileSystemService;
            SettingsService = multiplayerSettingsService;
            ValueGenerator = valueGenerator;
            _networkConnection = networkConnection;
        }

        public GameConnectivity GetGameConnectivity()
        {
            return Game?.Connectivity;
        }

        public List<NetworkPlayer> GetPlayers()
        {
            lock (ActionLock)
            {
                return [.. Game.Players ?? []];
            }
        }

        public List<NetworkCharacter> GetCharacters()
        {
            return [.. Game.Characters ?? []];
        }

        public IDictionary<NetworkPlayerControlledFeature, long> GetFeaturesControl()
        {
            return Game.FeatureControllers.ToDictionary(x => x.Key, x => x.Value);
        }

        public bool IsControlledByLocalPlayer(string unitId)
        {
            try
            {
                var character = GetPartyCharacter(unitId);
                var isControlled = character?.Owner != null && character.Owner.Id == Game.LocalPlayerId;

                if (!isControlled && GameInteraction.IsCapitalPartyMode)
                {
                    isControlled = WasControlledByCurrentPlayer(unitId);
                }

                return isControlled;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to determine if character is controlled by local player");
                throw;
            }
        }

        public bool IsControlledByPlayers(string unitId)
        {
            try
            {
                var character = GetPartyCharacter(unitId);

                return character?.Owner != null;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to determine if character is controlled by players");
                throw;
            }
        }

        public void CombatRoundStarted(int round)
        {
            if (IsSaveLoadPending) return;
            Logger.LogInformation("Combat round started. Round={Round}", round);
            Game.Combat.Round = round;
        }

        public void OnAbilityUse(NetworkAbilityUse abilityUse)
        {
            if (!ShouldNotifyAboutAbility(abilityUse.InitiatorUnitId))
            {
                return;
            }

            var message = new NotifyAbilityUsed
            {
                AbilityUse = Mapper.Map<Networking.Messages.Contracts.NetworkAbilityUse>(abilityUse)
            };
            Send(message);
        }

        public void OnUnitAttackCommandStarted(NetworkUnitAttack networkUnitAttack)
        {
            if (!IsControlledByLocalPlayer(networkUnitAttack.InitiatorUnitId) || Game.Combat?.Turn != null && Game.Combat.Turn.IsAI)
            {
                return;
            }

            var message = new NotifyUnitAttacked
            {
                Attack = Mapper.Map<Networking.Messages.Contracts.NetworkUnitAttack>(networkUnitAttack)
            };
            Send(message);
        }

        public void OnToggleActivatableAbility(NetworkActivatableAbility activatableAbilityUse)
        {
            if (!ShouldNotifyAboutAbility(activatableAbilityUse.CasterId))
            {
                return;
            }

            var message = new NotifyToggleActivatableAbility
            {
                Ability = Mapper.Map<Networking.Messages.Contracts.NetworkActivatableAbility>(activatableAbilityUse)
            };
            Send(message);
        }

        public void OnClickMapObject(NetworkClick click)
        {
            if (!IsControlledByLocalPlayer(click.SelectedUnits.FirstOrDefault()))
            {
                return;
            }

            var message = new NotifyMapObjectClicked
            {
                Click = Mapper.Map<Networking.Messages.Contracts.NetworkClick>(click)
            };
            Send(message);
        }

        public void OnTransferInventoryItem(NetworkItemsTransfer transferItem)
        {
            var message = new NotifyInventoryItemTransferred
            {
                TransferItem = Mapper.Map<Networking.Messages.Contracts.NetworkItemsTransfer>(transferItem)
            };
            Send(message);
        }

        public void OnSkinLootContainer(NetworkLootableEntity networkLootableEntity)
        {
            var message = new NotifyLootableEntitySkinned
            {
                Entity = Mapper.Map<Networking.Messages.Contracts.NetworkLootableEntity>(networkLootableEntity)
            };
            Send(message);
        }

        public void OnDropItem(NetworkDropItem dropItem)
        {
            var message = new NotifyDropItem
            {
                Drop = Mapper.Map<Networking.Messages.Contracts.NetworkDropItem>(dropItem)
            };
            Send(message);
        }

        public void OnUseInventoryItem(NetworkUseInventoryItem useInventoryItem)
        {
            // you can't use items from inventory in combat
            // but using them from action bar is triggering this method as well
            if (Game.Combat != null)
            {
                return;
            }

            var message = new NotifyInventoryItemUsed
            {
                UseItem = Mapper.Map<Networking.Messages.Contracts.NetworkUseInventoryItem>(useInventoryItem)
            };
            Send(message);
        }

        public void OnChangeActiveHandEquipmentSet(NetworkActiveHandEquipmentSet set)
        {
            if (!IsControlledByPlayers(set.UnitId) && !GameInteraction.IsUnitInParty(set.UnitId))
            {
                return;
            }

            var message = new NotifyActiveHandEquipmentSetChanged
            {
                Set = Mapper.Map<Networking.Messages.Contracts.NetworkActiveHandEquipmentSet>(set)
            };
            Send(message);
        }

        public void OnLootClosed(NetworkMapObject mapObject)
        {
            var message = new NotifyLootClosed
            {
                MapObject = Mapper.Map<Networking.Messages.Contracts.NetworkMapObject>(mapObject)
            };
            Send(message);
        }

        public void OnDungeonGameOverShown()
        {
            AddPlayerToTracker(Game.PlayersInDungeonGameOver, Game.LocalPlayerId);
            var message = new NotifyDungeonGameOverShown
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);

            UpdateDungeonGameOverUIState();
        }

        public void OnDungeonBoonSelectorShown()
        {
            AddPlayerToTracker(Game.PlayersInDungeonBoonSelector, Game.LocalPlayerId);
            var message = new NotifyDungeonBoonSelectorShown
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);

            UpdateDungeonBoonSelectorUIState();
        }

        public void OnTacticalCombatRetreat()
        {
            var message = new NotifyTacticalCombatRetreated();
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

        public bool OnTacticalCombatTotalDefenseUsed(string unitId)
        {
            var message = new NotifyTacticalCombatTotalDefenseUsed
            {
                UnitId = unitId
            };
            Send(message);
            return true;
        }

        public bool OnTacticalCombatTurnPostponed(string unitId)
        {
            var message = new NotifyTacticalCombatTurnPostponed
            {
                UnitId = unitId
            };
            Send(message);
            return true;
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

        public bool HasControlOverFeature(NetworkPlayerControlledFeature feature)
        {
            return Game.FeatureControllers.TryGetValue(feature, out var playerId) && playerId == Game.LocalPlayerId;
        }

        public void OnEquipmentSlotChanged(NetworkEquipmentSlot equipmentSlot)
        {
            if (!IsControlledByPlayers(equipmentSlot.OwnerId) && !GameInteraction.IsUnitInParty(equipmentSlot.OwnerId))
            {
                return;
            }

            var message = new NotifyEquipmentSlotChanged
            {
                Slot = Mapper.Map<Networking.Messages.Contracts.NetworkEquipmentSlot>(equipmentSlot)
            };
            Send(message);
        }

        public void OnInteractWithMapObjectOvertip(NetworkOvertip networkOvertip)
        {
            var message = new NotifyOvertipInteracted
            {
                Overtip = Mapper.Map<Networking.Messages.Contracts.NetworkOvertip>(networkOvertip)
            };
            Send(message);
        }

        public virtual void OnAreaLoaded()
        {
            Logger.LogInformation("OnAreaLoaded. AreaName={AreaName}", Game.CurrentArea?.Name);
            Game.ForcedPause.ReadyPlayers.Add(Game.LocalPlayerId);

            if (Game.CurrentArea.IsGlobalMap)
            {
                if (!Game.PlayersInGlobalMapMode.TryGetValue(Game.LocalPlayerId, out var mode))
                {
                    Logger.LogWarning("Traveler mode is not set yet");
                    mode = NetworkGlobalMapTravelerMode.Player;
                }

                OnGlobalMapTravelerModeChanged(mode);
            }
        }

        public void OnAreaLoadingComplete()
        {
            Game.CurrentArea = GameInteraction.GetCurrentArea();
            Logger.LogInformation("Area scenes loaded. Chapter={Chapter}, AreaName={AreaName}", Game.CurrentArea.Chapter, Game.CurrentArea.Name);

            SetLobbyStage(NetworkLobbyStage.Playing);

            SoftReset();

            UpdateCharactersOwnership();

            lock (ActionLock)
            {
                EnsureForcePaused(NetworkForcedPauseReason.AreaLoading);
                GameInteraction.SetPause(true);

                if (Game.CurrentArea.IsGlobalMap && GetSyncedPlayersCount() > 1)
                {
                    GlobalMapInteraction.DisableInteractions();
                }
            }
        }

        /// <summary>
        /// Reloads current party characters and tries to merge ownership
        /// </summary>
        public void UpdateCharactersOwnership()
        {
            Logger.LogInformation("Updating current characters & merging ownership");

            try
            {
                // could be synced from host, but state is the same anyway
                var partyCharacters = GameInteraction.GetPartyPlayers();
                if (partyCharacters.Count == 0)
                {
                    Logger.LogWarning("Can't update ownership due to empty party characters list");
                    return;
                }

                List<NetworkCharacter> oldCharacters = [.. Game.Characters];
                Game.Characters = [.. partyCharacters];
                var defaultOwner = GetHost();
                foreach (var character in Game.Characters)
                {
                    NetworkPlayer historicOwner = null;
                    if (Game.CharactersOwnershipHistory.TryGetValue(character.UnitId, out var playerId))
                    {
                        historicOwner = GetPlayer(playerId);
                    }

                    var owner = historicOwner
                        ?? oldCharacters.FirstOrDefault(old => !string.IsNullOrEmpty(old.UnitId) && old.UnitId == character.UnitId)?.Owner
                        ?? oldCharacters.FirstOrDefault(old => old.Index.HasValue && old.Index.Value == character.Index)?.Owner // this one is possible only on initial multiplayer game load when we don't have history yet due to missing UnitIds
                        ?? defaultOwner;

                    character.Owner = owner;
                    UpdateCharacterOwnershipHistory(character);
                    Logger.LogInformation("Character owner has been assigned. UnitId={UnitId}, CharacterName={CharacterName}, Owner={Owner}", character.UnitId, character.Name, character.Owner.Id);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to update characters ownership");
                throw;
            }
        }

        public void OnGameLoaded()
        {
            Logger.LogInformation("OnGameLoaded");
            // Tutorial settings are save-dependent, so they must be overridden if the save was created without the mod
            var settings = new NetworkGameSettings { Tutorial = new NetworkTutorialSettings() };
            GameInteraction.ApplyGameSettings(settings);
            if (PendingCombatSave != null)
            {
                if (!CombatSaving.CombatSaveLoadState.HoldsWorld(Kingmaker.Game.Instance?.State))
                    throw new InvalidOperationException("combat-save-game-loaded-world-mismatch");
                _combatSaveGameLoaded = true;
                Logger.LogInformation("[CombatSave] Native load lifecycle completed; awaiting final idle state.");
            }
            CompleteSaveLoad();
        }

        private DateTime _lastLocalPingAt;

        protected bool RegisterLocalDialogVote(NetworkDialogState state, string cueName, string answerName)
        {
            var repeated = state.LastLocalVoteRound == state.VoteRoundId
                && string.Equals(state.LastLocalVoteCue, cueName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(state.LastLocalVoteAnswer, answerName, StringComparison.OrdinalIgnoreCase);
            state.LastLocalVoteRound = state.VoteRoundId;
            state.LastLocalVoteCue = cueName;
            state.LastLocalVoteAnswer = answerName;
            return repeated;
        }

        protected void ShowRepeatedDialogVoteReminder(NetworkDialogState state)
        {
            var now = DateTime.UtcNow;
            if ((now - state.LastVoteReminderAt).TotalSeconds < 2) return;
            state.LastVoteReminderAt = now;
            PlayerNotification.ShowWarningNotification(WellKnownKeys.GameNotifications.Dialogs.WaitingForAgreement.Key,
                addToLog: false, warningDuration: 2f);
        }

        public void OnPing(NetworkPing ping)
        {
            if (Game == null || ping == null) return;
            var now = DateTime.UtcNow;
            if ((now - _lastLocalPingAt).TotalMilliseconds < 400) return;
            _lastLocalPingAt = now;
            var message = new NotifyPingedByPlayer
            {
                PlayerId = Game.LocalPlayerId,
                Ping = Mapper.Map<Networking.Messages.Contracts.NetworkPing>(ping),
                SessionId = Game.Id,
                LoadedSaveSeed = Game.LoadedSaveSeed,
                AreaSeed = AreaSeed
            };
            Send(message);

            PingInteraction.Create(GetPlayer(Game.LocalPlayerId), ping);
            Logger.LogInformation("Local map ping sent. PlayerId={PlayerId}, AreaSeed={AreaSeed}, Position={Position}",
                Game.LocalPlayerId, AreaSeed, ping.WorldPosition);
        }


        public void OnCutsceneSkip()
        {
            var localPlayer = GetPlayer(Game.LocalPlayerId);
            var message = new NotifyCutsceneSkipped { PlayerId = localPlayer.Id };
            Send(message);

            PlayerNotification.ShowWarningNotification(WellKnownKeys.GameNotifications.Cutscenes.Skipped.Key, args: localPlayer.Name);
        }

        public void ForceLoadGame(string gameId, string savePath)
        {
            if (IsSaveLoadPending)
            {
                // LoadSavedGame queues Game.LoadGame after preparing the network
                // load. Admit that native entry once without starting another transfer.
                if (_nativeSaveLoadStarted && !_nativeSaveLoadAdmitted && Game.Id == gameId
                    && string.Equals(Path.GetFullPath(Game.StartUp.SavePath), Path.GetFullPath(savePath), StringComparison.OrdinalIgnoreCase))
                {
                    _nativeSaveLoadAdmitted = true;
                    return;
                }
                throw new InvalidOperationException("save-load-already-pending");
            }
            if (Game.Stage != NetworkLobbyStage.Playing && Game.Stage != NetworkLobbyStage.Lobby)
            {
                throw new InvalidOperationException("save-load-stage-unavailable");
            }

            if (WOTRMultiplayer.Services.GameInteraction.RealTimeNativeLoadLease.IsActive
                || Game.RealTimeCombat.Phase != CombatSynchronizationPhase.Stable || Game.RealTimeCombat.CombatEndPending)
            {
                Logger.LogWarning("[SaveLoad] Load refused while a world replacement or combat barrier owns the world.");
                PlayerNotification.ShowWarningNotification(WellKnownKeys.LobbyWindow.RealTime.ModeUnavailable.Key);
                throw new InvalidOperationException("save-load-combat-barrier-active");
            }

            PrepareCombatSaveLoad(savePath);

            Game.StartUp = new NetworkGameStartUp
            {
                SavePath = savePath,
                AutoStart = true,
                IsNewGameSequence = false,
            };
            Game.Id = gameId;

            ResetRealTimeRuleRolls();

            lock (ActionLock)
            {
                Game.ForcedPause = null;
            }

            ResetOnSaveLoad();

            Game.LoadedSaveSeed = CreateRandomSeed();
            BeginSaveLoad();
            // This method is called by Game.LoadGame's prefix: the host is
            // already entering its native load, unlike a peer awaiting chunks.
            _nativeSaveLoadStarted = true;
            _nativeSaveLoadAdmitted = true;

            TransferSaveGame(requiresProgress: false);
        }

        public void CombatStarted()
        {
            if (IsSaveLoadPending) return;
            if (CombatSaving.CombatSaveLoadState.Active) return;
            if (HasCombatStartupFailure) { GameInteraction.SetPause(true); return; }
            if (Game?.Combat != null && HoldsCombatMembership) return;
            _combatSaveSemanticSeeds = null;
            if (TryRetainRealTimeCombatOnStart()) return;
            if (RealTimePresentationRuntime.Enabled && Game?.RealTimeCombat?.CombatEndPending == true)
            {
                GameInteraction.SetPause(true);
                Logger.LogError("A new combat started before the previous final checkpoint was confirmed; retaining the protected encounter.");
                return;
            }
            Logger.LogInformation("Combat started");
            ResetRealTimeRuleRolls();
            if (Game.Combat != null)
            {
                Logger.LogWarning("Previous combat has not been disposed correctly");
            }

            ResetManualPause();

            Game.Combat = new NetworkCombat
            {
                StartedAt = DateTime.UtcNow,
                RealTimeBootstrapPending = Game.RealTimeCombat.PreferredMode == CombatMode.RealTimeWithPause
            };
            // A new encounter needs its own RTwP handshake, even if the native
            // engine already has real-time selected from outside combat.
            Game.RealTimeCombat.Mode = CombatMode.TurnBased;
            if (Game.Combat.RealTimeBootstrapPending) GameInteraction.SetPause(true);
            ResetLastCombatTurn();
        }

        public void ForceCombatEnd()
        {
            CombatInteraction.ForceResetCombat();
        }

        public void CombatEnded()
        {
            if (IsSaveLoadPending) return;
            if (IsNativeCombatRestart) return;
            if (IsCombatStartupBlocked)
            {
                FailCombatStartup("native-combat-ended-during-initialization");
                return;
            }
            if (TryDeferRealTimeCombatEnd()) return;
            CompleteCombatEnd();
        }

        protected virtual bool TryDeferRealTimeCombatEnd() => false;

        protected virtual bool TryRetainRealTimeCombatOnStart() => false;

        protected void CompleteCombatEnd()
        {
            try
            {
                ResetRealTimeRuleRolls();
                if (Game.Combat == null)
                {
                    Logger.LogWarning("Combat has not been started correctly");
                }

                Logger.LogInformation("Combat ended");

                SaveLastCombatTurn();
                ResetManualPause();

                CleanupUntargetableUnitsState();

                ValueGenerator.ResetSeededGenerators(IdentifierLifetime.Combat, IdentifierLifetime.CombatTurn);

                Game.Combat = null;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error ending combat");
                throw;
            }
        }

        protected virtual void ResetRealTimeRuleRolls() { }

        protected async Task ApplyOutsideCombatPreferenceAsync(Func<bool> isActive)
        {
            var session = Game;
            var state = session?.RealTimeCombat;
            if (!isActive() || state == null || session.Stage != NetworkLobbyStage.Playing
                || session.Combat != null
                || state.CombatEndPending || state.Phase != CombatSynchronizationPhase.Stable) return;
            var sequence = state.PreferenceSequence;
            var saveSeed = session.LoadedSaveSeed;
            var mode = state.PreferredMode;
            try
            {
                var applied = await GameInteraction.TrySetTurnBasedCombatModeAsync(mode == CombatMode.TurnBased,
                    () => isActive() && ReferenceEquals(Game, session) && ReferenceEquals(session.RealTimeCombat, state)
                        && session.LoadedSaveSeed == saveSeed && state.PreferenceSequence == sequence
                        && session.Stage == NetworkLobbyStage.Playing
                        && session.Combat == null
                        && state.Phase == CombatSynchronizationPhase.Stable && !state.CombatEndPending
                        && Kingmaker.Game.Instance?.Player?.IsInCombat == false);
                if (applied && ReferenceEquals(Game, session) && state.PreferenceSequence == sequence
                    && session.Combat == null)
                {
                    state.Mode = mode;
                    Logger.LogInformation("[CombatMode] Outside-combat engine selection and button updated. Mode={Mode}, Sequence={Sequence}", mode, sequence);
                }
            }
            catch (Exception failure)
            {
                Logger.LogError(failure, "[CombatMode] Outside-combat mode could not be applied.");
                PlayerNotification.ShowWarningNotification(WellKnownKeys.LobbyWindow.RealTime.ModeUnavailable.Key);
            }
        }

        public void OnHandleDelayCombatTurn(string unitId, string targetUnitId)
        {
            if (!IsControlledByLocalPlayer(unitId))
            {
                return;
            }

            var message = new NotifyCombatTurnDelayed
            {
                UnitId = unitId,
                TargetUnitId = targetUnitId
            };
            Send(message);

            PlayerNotification.AddCombatText(WellKnownKeys.GameNotifications.Combat.Turn.Delayed.Key, CombatTextSeverity.Common, new UnitLogParameter(unitId));
        }

        public bool OnBeforeTurnStart(string unitId, bool actingInSurpriseRound)
        {
            if (CombatSaving.CombatSavePauseLease.Active || CombatSaving.CombatSaveLoadState.Active) return false;
            try
            {
                Logger.LogInformation("Trying to start turn. TurnInitialized={TurnInitialized}, UnitId={UnitId}, ActingInSurpriseRound={ActingInSurpriseRound}", Game.Combat.Turn != null, unitId, actingInSurpriseRound);
                if (Game.Combat.Turn == null)
                {
                    InitializeNewTurn(unitId, actingInSurpriseRound);
                    return false;
                }

                switch (Game.Combat.Turn.Stage)
                {
                    case NetworkCombatTurnStage.Playing:
                        if (!string.Equals(Game.Combat.Turn.UnitId, unitId, StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.LogWarning("Invalid unit turn start detected. ExpectedUnitId={ExpectedUnitId}, ActualUnitId={ActualUnitId}", Game.Combat.Turn.UnitId, unitId);
                            InitializeNewTurn(unitId, actingInSurpriseRound);
                            return false;
                        }

                        UpdateConfirmedMidCombatUnits();
                        PlayerNotification.AddCombatText(WellKnownKeys.GameNotifications.Combat.Turn.Started.Key, CombatTextSeverity.Common, new UnitLogParameter(unitId));
                        Logger.LogInformation("Turn start is allowed. UnitId={UnitId}, IsActingInSurpriseRound={IsActingInSurpriseRound}, TurnUnitId={TurnUnitId}, TurnSeed={TurnSeed}", unitId, actingInSurpriseRound, Game.Combat.Turn.UnitId, Game.Combat.Turn.Seed);
                        return true;
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to start turn");
                throw;
            }
        }

        public bool OnBeforeTurnEnd(string unitId)
        {
            if (CombatSaving.CombatSavePauseLease.Active || CombatSaving.CombatSaveLoadState.Active) return false;
            try
            {
                // turn has been forcefully ended due to auto-kill
                if (Game.Combat.Turn == null)
                {
                    return false;
                }

                switch (Game.Combat.Turn.Stage)
                {
                    case NetworkCombatTurnStage.Playing:
                        var message = new NotifyCombatLocalTurnEnded
                        {
                            UnitId = Game.Combat.Turn.UnitId,
                            PlayerId = Game.LocalPlayerId
                        };
                        Send(message);
                        SetCombatTurnStage(NetworkCombatTurnStage.Ending);
                        OnLocalPlayerTurnEnd();
                        return false;
                    case NetworkCombatTurnStage.Ended:
                        OnTurnEnded(unitId);
                        return true;
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to process turn end. UnitId={UnitId}", unitId);
                throw;
            }
        }

        public SeededContext GetSeededContext(SeedKind seedKind)
        {
            var idBuilder = new StringBuilder();
            foreach (var seed in AllSeeds)
            {
                IncludeSeed(idBuilder, seedKind, seed);
            }

            var lifetime = CombatTurnSeed == null ? IdentifierLifetime.Area : IdentifierLifetime.CombatTurn;
            var context = new SeededContext
            {
                Id = idBuilder.ToString(),
                Lifetime = lifetime
            };

            return context;
        }

        public bool CanUnitJoinCombat(string unitId, string groupId)
        {
            try
            {
                if (Game.Combat == null
                    || Game.Combat.Stage == NetworkCombatStage.Initiating
                    || Game.Combat.Stage == NetworkCombatStage.Reconciling
                    || GameInteraction.IsUnitInParty(unitId))
                {
                    Logger.LogInformation("Unit is allowed to join combat at current stage. UnitId={UnitId}, Stage={Stage}", unitId, Game.Combat?.Stage);
                    return true;
                }

                var isSummoned = GameInteraction.IsSummoned(unitId);
                if (isSummoned)
                {
                    Logger.LogInformation("Summoned unit is allowed to join combat. UnitId={UnitId}", unitId);
                    return true;
                }

                if (Game.Combat.ConfirmedMidCombatUnits.Contains(unitId))
                {
                    MakeUnitTargetable(unitId, groupId);
                    Logger.LogWarning("Unit has been allowed to join mid combat. UnitId={UnitId}", unitId);
                    return true;
                }

                var isFirstJoinEvent = !ConfirmReadiness(PlayerTurnReadinessType.UnitJoinedMidCombat, Game.LocalPlayerId, unitId);
                if (isFirstJoinEvent)
                {
                    var message = new NotifyUnitJoinedMidCombat { UnitId = unitId, PlayerId = Game.LocalPlayerId };
                    Send(message);
                }

                AddPlayerReadyStatus(PlayerTurnReadinessType.UnitJoinedMidCombat, Game.LocalPlayerId, unitId);
                MakeUnitUntargetable(unitId, groupId);

                return false;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to process unit join combat request. UnitId={UnitId}", unitId);
                throw;
            }
        }

        public string GetCharacterOwnerName(string unitId)
        {
            var character = GetPartyCharacter(unitId);
            return character?.Owner?.Name;
        }

        public void OnStartGameMode(GameModeType type)
        {
            var isFirstTime = RegisterGameMode(type, Game.LocalPlayerId);
            if (!isFirstTime)
            {
                return;
            }

            if (type == GameModeType.Rest)
            {
                UpdateRestUIState();
            }

            // game removes pause in this cases
            if (type == GameModeType.Dialog || type == GameModeType.Cutscene)
            {
                ResetManualPause();
            }

            var message = new NotifyGameModeTypeStarted { PlayerId = Game.LocalPlayerId, Type = type.Name };
            Send(message);
        }

        public void OnStopGameMode(GameModeType type)
        {
            var isFirstTime = UnregisterGameMode(type, Game.LocalPlayerId);
            if (!isFirstTime)
            {
                return;
            }

            if (type == GameModeType.Rest)
            {
                lock (ActionLock)
                {
                    OnLocalRestGameModeEnded();
                }
            }
            else if (type == GameModeType.Dialog)
            {
                Game.DialogState = null;
            }

            var message = new NotifyGameModeTypeEnded { PlayerId = Game.LocalPlayerId, Type = type.Name };
            Send(message);
        }

        public void OnCapitalModeRest()
        {
            var message = new NotifyCapitalModeRestInitiated();
            Send(message);
        }

        public virtual void OnStartRest()
        {
            Game.Rest = new NetworkRest();
        }

        public void OnStartRestSleepPhase()
        {
            lock (ActionLock)
            {
                Game.Rest.SleepPhase++;
                Logger.LogInformation("Rest sleep phase has been updated. SleepPhase={SleepPhase}", Game.Rest.SleepPhase);
            }
        }

        public void OnShowRestView(RestPhase phase)
        {
            if (phase != RestPhase.ShowingResults)
            {
                return;
            }

            lock (ActionLock)
            {
                AddPlayerToTracker(Game.Rest.PlayersFinishedRest, Game.LocalPlayerId);
                UpdateRestResultsUIState();
            }

            var message = new NotifyRestEnded { PlayerId = Game.LocalPlayerId };
            Send(message);
        }

        public void OnRestBanterBarkInterrupted(NetworkRestBanter networkBanter)
        {
            var message = new NotifyRestBanterInterrupted
            {
                Banter = Mapper.Map<Networking.Messages.Contracts.NetworkRestBanter>(networkBanter),
            };

            Send(message);
        }

        public void OnTransferVendorItem(NetworkVendorItemTransfer transfer)
        {
            var message = new NotifyVendorItemTransferred
            {
                ItemTransfer = Mapper.Map<Networking.Messages.Contracts.NetworkVendorItemTransfer>(transfer)
            };
            Send(message);
        }

        public void OnMemorizeSpell(string unitId, NetworkSpellSlot networkSpellSlot, NetworkAbility networkAbility)
        {
            var message = new NotifySpellMemorized
            {
                UnitId = unitId,
                Slot = Mapper.Map<Networking.Messages.Contracts.NetworkSpellSlot>(networkSpellSlot),
                Ability = Mapper.Map<Networking.Messages.Contracts.NetworkAbility>(networkAbility),
            };
            Send(message);
        }

        public void OnForgetSpell(string unitId, NetworkSpellSlot networkSpellSlot, NetworkAbility networkAbility)
        {
            var message = new NotifySpellForgotten
            {
                UnitId = unitId,
                Slot = Mapper.Map<Networking.Messages.Contracts.NetworkSpellSlot>(networkSpellSlot),
                Ability = Mapper.Map<Networking.Messages.Contracts.NetworkAbility>(networkAbility),
            };
            Send(message);
        }

        public void OnSwapMemorizedSlots(string unitId, string spellbookId, int spellLevel, NetworkSpellSlot spellSlotA, NetworkSpellSlot spellSlotB)
        {
            var message = new NotifySpellSlotsSwapped
            {
                UnitId = unitId,
                SpellbookId = spellbookId,
                SpellLevel = spellLevel,
                SlotA = Mapper.Map<Networking.Messages.Contracts.NetworkSpellSlot>(spellSlotA),
                SlotB = Mapper.Map<Networking.Messages.Contracts.NetworkSpellSlot>(spellSlotB),
            };
            Send(message);
        }

        public void OnRemoveCustomSpell(string unitId, NetworkAbility ability)
        {
            if (!IsControlledByLocalPlayer(unitId))
            {
                return;
            }

            var message = new NotifyCustomSpellRemoved
            {
                Ability = Mapper.Map<Networking.Messages.Contracts.NetworkAbility>(ability),
                UnitId = unitId
            };
            Send(message);
        }

        public void OnSpellbookMetamagicSpellCreated(NetworkMetamagicSpell metamagicSpell)
        {
            var message = new NotifyMetamagicSpellCreated
            {
                MetamagicSpell = Mapper.Map<Networking.Messages.Contracts.NetworkMetamagicSpell>(metamagicSpell)
            };
            Send(message);
        }

        public void OnSpellbookMagicHackSpellCreated(NetworkMagicHackSpell magicHackSpell)
        {
            var message = new NotifyMagicHackSpellCreated
            {
                MagicHackSpell = Mapper.Map<Networking.Messages.Contracts.NetworkMagicHackSpell>(magicHackSpell)
            };
            Send(message);
        }

        /// <summary>
        /// Unlike OnRequestLevelingUI, this method is used when game is enforcing opening leveling ui
        /// State should be the same across players since it's not a result of their action
        /// Also, we shouldn't deny opening it as it contains few 'OnStop' actions
        /// E.g. dialog -> mythic path selection at the end of act 2 -> dialog
        /// </summary>
        /// <param name="unitId"></param>
        /// <param name="networkLevelingType"></param>
        public void OnForceLevelingUI(string unitId, NetworkLevelingType networkLevelingType)
        {
            lock (ActionLock)
            {
                if (HasLevelingSynchronizationFailure) return;
                var pending = _deferredLevelings.Values.FirstOrDefault(value =>
                    ReferenceEquals(value.Session, Game) && IsCurrentLevelingIdentity(value.Identity)
                    && value.UnitId == unitId && value.Type == networkLevelingType && value.AwaitingForcedUI);
                if (pending != null)
                {
                    if (Game.Leveling == null)
                        InitiateLeveling(unitId, networkLevelingType, pending.Identity);
                    AdoptForcedLeveling(pending.Identity, networkLevelingType);
                    return;
                }
                if (Game.Leveling?.IsForced == true && Game.Leveling.UnitId == unitId && Game.Leveling.Type == networkLevelingType)
                    return;
                if (Game.Leveling != null && (Game.Leveling.UnitId != unitId || Game.Leveling.Type != networkLevelingType))
                {
                    FailLevelingSynchronization(Game, Game.Leveling.Identity,
                        new InvalidOperationException("forced-leveling-interrupted-local-leveling"));
                    return;
                }
                if (Game.Leveling == null || Game.Leveling.UnitId != unitId || Game.Leveling.Type != networkLevelingType)
                    InitiateLeveling(unitId, networkLevelingType);
                Game.Leveling.IsForced = true;
                var host = Game.LocalPlayerId == WOTRMultiplayer.Networking.NetworkConstants.HostPlayerId;
                Game.Leveling.AwaitingHostIdentity = !host;
                if (host)
                {
                    Game.Leveling.Identity.OwnerId = Game.Players.FirstOrDefault(player =>
                        CanControlLeveling(player.Id, unitId, networkLevelingType))?.Id ?? Game.LocalPlayerId;
                    Send(new NotifyLevelingStarted { UnitId = unitId, Type = networkLevelingType.ToString(),
                        Leveling = Game.Leveling.Identity, IsForced = true });
                }
            }
            Logger.LogInformation("Leveling UI has been forced by the game. UnitId={UnitId}, Type={Type}", Game.Leveling.UnitId, Game.Leveling.Type);
        }

        protected void ReceiveForcedLeveling(NetworkLevelingIdentity identity, NetworkLevelingType type)
        {
            lock (ActionLock)
            {
                if (Game.Leveling?.IsForced == true && Game.Leveling.UnitId == identity.UnitId && Game.Leveling.Type == type)
                {
                    AdoptForcedLeveling(identity, type);
                    return;
                }
                if (!_deferredLevelings.TryGetValue(identity.Id, out var pending))
                {
                    pending = new DeferredLevelingSession { Session = Game, Identity = identity,
                        OwnerId = identity.OwnerId, UnitId = identity.UnitId, Type = type };
                    _deferredLevelings.Add(identity.Id, pending);
                }
                if (ReferenceEquals(pending, _activeDeferredLeveling))
                {
                    pending.Failed = true;
                    FailLevelingSynchronization(Game, identity, new InvalidOperationException("forced-leveling-overlaps-replay"));
                    return;
                }
                pending.AwaitingForcedUI = true;
            }
        }

        private void AdoptForcedLeveling(NetworkLevelingIdentity identity, NetworkLevelingType type)
        {
            if (Game.Leveling == null || Game.Leveling.UnitId != identity.UnitId || Game.Leveling.Type != type)
            {
                FailLevelingSynchronization(Game, identity, new InvalidOperationException("forced-leveling-overlaps-local-leveling"));
                return;
            }
            var session = Game;
            var leveling = Game.Leveling;
            leveling.Identity = identity;
            leveling.IsForced = true;
            leveling.AwaitingHostIdentity = false;
            if (_deferredLevelings.TryGetValue(identity.Id, out var pending))
            {
                if (ReferenceEquals(pending, _activeDeferredLeveling) || pending.Failed)
                {
                    FailLevelingSynchronization(session, identity, new InvalidOperationException("forced-leveling-replay-not-quiescent"));
                    return;
                }
                _deferredLevelings.Remove(identity.Id);
                while (pending.Messages.Count > 0)
                {
                    var entry = pending.Messages.Dequeue();
                    DispatchLiveLeveling(session, leveling, identity, entry.Apply);
                }
            }
            if (ReferenceEquals(Game.Leveling, leveling))
                OnLevelingWitnessPhase(new NetworkLevelingPhase { Index = leveling.PhaseIndex });
        }

        public void OnLevelingMythicClassSelected(string mythicClassId)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingMythicClassSelected
            {
                MythicClassId = mythicClassId
            };
            Send(message);
        }

        public void OnLevelingClassSelected(NetworkLevelingClass levelingClass)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingClassSelected
            {
                Class = Mapper.Map<Networking.Messages.Contracts.NetworkLevelingClass>(levelingClass)
            };
            Send(message);
        }

        public void OnLevelingClassArchetypeSelected(NetworkLevelingArchetype archetype)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingClassArchetypeSelected
            {
                Archetype = Mapper.Map<Networking.Messages.Contracts.NetworkLevelingArchetype>(archetype)
            };
            Send(message);
        }

        public bool CanMakeLevelingDecisions()
        {
            if (Game.Leveling == null || Game.Leveling.AwaitingHostIdentity || HasLevelingSynchronizationFailure)
            {
                return false;
            }

            if (!CanControlLeveling(Game.LocalPlayerId, Game.Leveling.UnitId, Game.Leveling.Type))
            {
                return false;
            }

            // Ordinary companion level-ups are owned by one player. Requiring every
            // other PC to open and witness the same modal is what blurred/blocked the
            // remote game and made two independent level-ups impossible.
            return IsIndependentLeveling(Game.Leveling.Type)
                || Game.Leveling.PlayerReadiness.Count >= GetSyncedPlayersCount();
        }

        // Visibility follows ownership, NOT phase readiness: waiting for an ACK
        // must never make the owner's window disappear.
        public bool ShouldShowLevelingUI() => Game.Leveling == null
            || CanControlLeveling(Game.LocalPlayerId, Game.Leveling.UnitId, Game.Leveling.Type);

        protected bool CanControlLeveling(long playerId, string unitId, NetworkLevelingType type)
        {
            var hostId = Game.Players.FirstOrDefault(p => p.IsHost)?.Id;
            var ownerId = type switch
            {
                NetworkLevelingType.NewGameSequence => Game.Characters.FirstOrDefault()?.Owner?.Id,
                NetworkLevelingType.Mercenary => hostId,
                _ => GetPartyCharacter(unitId)?.Owner?.Id,
            };
            if (!ownerId.HasValue && type != NetworkLevelingType.NewGameSequence && type != NetworkLevelingType.Mercenary)
            {
                var realUnitId = GameInteraction.GetPetOwnerId(unitId) ?? unitId;
                if (!string.IsNullOrEmpty(realUnitId) && Game.CharactersOwnershipHistory.TryGetValue(realUnitId, out var previousOwner))
                    ownerId = previousOwner;
            }
            // A disconnected owner no longer blocks the host. Pets use their
            // master's assignment via GetPartyCharacter/GetPetOwnerId.
            if (!ownerId.HasValue || GetPlayer(ownerId.Value) == null) ownerId = hostId;
            return ownerId.HasValue && ownerId.Value == playerId;
        }

        private static bool IsIndependentLeveling(NetworkLevelingType type)
            => type == NetworkLevelingType.Leveling || type == NetworkLevelingType.MythicLeveling;

        protected NetworkLevelingIdentity CreateLevelingIdentity(long ownerId, string unitId, NetworkLevelingType type)
            => new()
            {
                Id = Guid.NewGuid().ToString("N"), SessionId = Game.Id,
                LoadedSaveSeed = Game.LoadedSaveSeed, OwnerId = ownerId,
                UnitId = unitId, Type = type.ToString()
            };

        protected bool IsCurrentLevelingIdentity(NetworkLevelingIdentity identity)
        {
            var session = Game;
            return identity != null && Guid.TryParseExact(identity.Id, "N", out _)
                && session != null && identity.SessionId == session.Id
                && identity.LoadedSaveSeed == session.LoadedSaveSeed;
        }

        private static bool SameLevelingIdentity(NetworkLevelingIdentity expected, NetworkLevelingIdentity received)
            => expected != null && received != null && expected.Id == received.Id
                && expected.SessionId == received.SessionId && expected.LoadedSaveSeed == received.LoadedSaveSeed
                && expected.UnitId == received.UnitId && expected.Type == received.Type && expected.OwnerId == received.OwnerId;

        protected void StampLevelingMessage(object message)
        {
            if (message is ILevelingMessage leveling && leveling.Leveling == null)
                leveling.Leveling = Game?.Leveling?.Identity;
        }

        protected NetworkLevelingIdentity GetLevelingIdentityForStart(string unitId, NetworkLevelingType type)
            => _deferredLevelings.Values.FirstOrDefault(value =>
                ReferenceEquals(value.Session, Game) && value.UnitId == unitId && value.Type == type
                && !value.HasTerminalMessage)?.Identity
                ?? Game.Leveling?.Identity;

        protected bool DeferRemoteLeveling(long ownerId, string unitId, NetworkLevelingType type,
            NetworkLevelingIdentity identity = null)
        {
            if (!IsIndependentLeveling(type) || string.IsNullOrWhiteSpace(unitId)
                || CanControlLeveling(Game.LocalPlayerId, unitId, type))
            {
                return false;
            }

            lock (ActionLock)
            {
                identity ??= _deferredLevelings.Values.FirstOrDefault(value =>
                    ReferenceEquals(value.Session, Game) && value.UnitId == unitId
                    && value.OwnerId == ownerId && value.Type == type && !value.HasTerminalMessage)?.Identity
                    ?? CreateLevelingIdentity(ownerId, unitId, type);
                if (!_deferredLevelings.TryGetValue(identity.Id, out var deferred))
                {
                    deferred = new DeferredLevelingSession
                    {
                        Session = Game,
                        OwnerId = ownerId,
                        UnitId = unitId,
                        Type = type,
                        Identity = identity
                    };
                    _deferredLevelings.Add(identity.Id, deferred);
                    Logger.LogInformation("[LevelingOwnerUI] Deferring remote level-up replay. Owner={Owner}, UnitId={UnitId}, Type={Type}",
                        ownerId, unitId, type);
                }
                return ReferenceEquals(deferred.Session, Game) && deferred.OwnerId == ownerId
                    && deferred.UnitId == unitId && deferred.Type == type;
            }
        }

        private Action<long, T> DeferLevelingMessages<T>(Action<long, T> handler) where T : class
            => (receivedFrom, message) =>
                DispatchLevelingMessage(() => ApplyLevelingMessage(receivedFrom, message, handler));

        // Starts and subsequent messages must cross the same FIFO before looking
        // up an active identity. Otherwise a choice can overtake its queued start.
        protected void DispatchLevelingMessage(Action apply)
        {
            NetworkGame session;
            int? saveSeed;
            long generation;
            lock (ActionLock)
            {
                session = Game;
                saveSeed = Game?.LoadedSaveSeed;
                generation = _levelingDispatchGeneration;
            }
            MainThreadAccessor.Post(() =>
            {
                lock (ActionLock)
                {
                    if (session == null || !ReferenceEquals(Game, session)
                        || Game.LoadedSaveSeed != saveSeed || generation != _levelingDispatchGeneration) return;
                    apply();
                }
            });
        }

        private void ApplyLevelingMessage<T>(long receivedFrom, T message, Action<long, T> handler) where T : class
        {
            NetworkGame session;
            NetworkLeveling leveling;
            NetworkLevelingIdentity identity;
            lock (ActionLock)
            {
                identity = (message as ILevelingMessage)?.Leveling;
                if (!IsCurrentLevelingIdentity(identity) || HasLevelingSynchronizationFailure) return;
                // The transport does not relay progressions. Only validated
                // owner choices and authentic phase witnesses reach peers.
                if (Game.LocalPlayerId == WOTRMultiplayer.Networking.NetworkConstants.HostPlayerId
                    && (message is NotifyLevelingPhaseWitnessed witness
                        ? witness.PlayerId != receivedFrom || !Game.Players.Any(player => player.Id == receivedFrom)
                        : receivedFrom != identity.OwnerId)) return;
                if (_deferredLevelings.TryGetValue(identity.Id, out var deferred)
                    && ReferenceEquals(deferred.Session, Game)
                    && SameLevelingIdentity(deferred.Identity, identity))
                {
                    if (deferred.HasTerminalMessage || deferred.Failed) return;
                    deferred.Messages.Enqueue((message, () => handler(receivedFrom, message)));
                    if (message is NotifyLevelingCompleted || message is NotifyLevelingTerminated)
                    {
                        deferred.HasTerminalMessage = true;
                    }
                    ForwardValidatedLevelingMessage(receivedFrom, message);
                    TryReplayDeferredLeveling();
                    return;
                }
                if (!SameLevelingIdentity(Game?.Leveling?.Identity, identity)) return;
                session = Game;
                leveling = Game.Leveling;
            }

            DispatchLiveLeveling(session, leveling, identity, () =>
            {
                ForwardValidatedLevelingMessage(receivedFrom, message);
                handler(receivedFrom, message);
            });
        }

        private void ForwardValidatedLevelingMessage(long receivedFrom, object message)
        {
            if (_networkConnection is INetworkHostConnection host
                && message is Networking.Messages.IForwardableMessage)
                host.BroadcastExcept(receivedFrom, message);
        }

        protected void StartOrQueueOwnedLeveling(NetworkLevelingIdentity identity, NetworkLevelingType type)
        {
            lock (ActionLock)
            {
                if (SameLevelingIdentity(Game.Leveling?.Identity, identity)) return;
                if (_deferredLevelings.ContainsKey(identity.Id)) return;
                if (Game.Leveling != null || _replayingDeferredLeveling)
                {
                    _deferredLevelings.Add(identity.Id, new DeferredLevelingSession
                    {
                        Session = Game, Identity = identity, OwnerId = identity.OwnerId,
                        UnitId = identity.UnitId, Type = type, AwaitingOwnedUI = true
                    });
                    return;
                }
                BeginOwnedLeveling(identity, type);
            }
        }

        private void BeginOwnedLeveling(NetworkLevelingIdentity identity, NetworkLevelingType type)
        {
            InitiateLeveling(identity.UnitId, type, identity);
            var session = Game;
            var leveling = Game.Leveling;
            DispatchLiveLeveling(session, leveling, identity,
                () => LevelingInteraction.StartLeveling(leveling.UnitId, leveling.Type));
        }

        protected void DispatchLiveLeveling(NetworkGame session, NetworkLeveling leveling,
            NetworkLevelingIdentity identity, Action apply)
        {
            bool Current() => ReferenceEquals(Game, session) && ReferenceEquals(Game?.Leveling, leveling)
                && IsCurrentLevelingIdentity(identity) && SameLevelingIdentity(leveling.Identity, identity)
                && !HasLevelingSynchronizationFailure;
            MainThreadAccessor.Post(() =>
            {
                if (!Current()) return;
                try
                {
                    using (LevelingInteraction.EnterReplayScope(Current,
                        error => FailLevelingSynchronization(session, identity, error))) apply();
                }
                catch (Exception error) { FailLevelingSynchronization(session, identity, error); }
            });
        }

        private void TryReplayDeferredLeveling()
        {
            DeferredLevelingSession deferred;
            lock (ActionLock)
            {
                if (_replayingDeferredLeveling || Game?.Leveling != null || HasLevelingSynchronizationFailure) return;
                var owned = _deferredLevelings.Values.FirstOrDefault(value =>
                    ReferenceEquals(value.Session, Game) && IsCurrentLevelingIdentity(value.Identity)
                    && value.AwaitingOwnedUI && !value.AwaitingForcedUI && !value.Failed);
                if (owned != null)
                {
                    if (!owned.OwnedStartScheduled)
                    {
                        owned.OwnedStartScheduled = true;
                        DispatchLevelingMessage(() =>
                        {
                            owned.OwnedStartScheduled = false;
                            if (Game.Leveling != null || _replayingDeferredLeveling || owned.AwaitingForcedUI || HasLevelingSynchronizationFailure
                                || !_deferredLevelings.TryGetValue(owned.Identity.Id, out var pending)
                                || !ReferenceEquals(pending, owned)) return;
                            _deferredLevelings.Remove(owned.Identity.Id);
                            BeginOwnedLeveling(owned.Identity, owned.Type);
                            var session = Game;
                            var leveling = Game.Leveling;
                            while (owned.Messages.Count > 0)
                                DispatchLiveLeveling(session, leveling, owned.Identity, owned.Messages.Dequeue().Apply);
                        });
                    }
                    return;
                }
                deferred = _deferredLevelings.Values.FirstOrDefault(value =>
                    ReferenceEquals(value.Session, Game) && IsCurrentLevelingIdentity(value.Identity)
                    && value.HasTerminalMessage && !value.AwaitingForcedUI && !value.Failed);
                if (deferred == null) return;

                _activeDeferredLeveling = deferred;
                _replayingDeferredLeveling = true;
            }

            _ = ReplayDeferredLevelingAsync(deferred);
        }

        private Task<bool> RunDeferredLevelingActionAsync(DeferredLevelingSession deferred, Action action)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            MainThreadAccessor.Post(() =>
            {
                try
                {
                    lock (ActionLock)
                    {
                        if (!ReferenceEquals(Game, deferred.Session)
                            || !ReferenceEquals(_activeDeferredLeveling, deferred))
                        {
                            completion.TrySetResult(false);
                            return;
                        }
                    }
                    using (LevelingInteraction.EnterReplayScope(() =>
                        ReferenceEquals(Game, deferred.Session)
                        && ReferenceEquals(_activeDeferredLeveling, deferred)
                        && IsCurrentLevelingIdentity(deferred.Identity) && !deferred.Failed && !HasLevelingSynchronizationFailure,
                        error =>
                        {
                            deferred.Failed = true;
                            FailLevelingSynchronization(deferred.Session, deferred.Identity, error);
                        }))
                        action();
                    completion.TrySetResult(true);
                }
                catch (Exception error) { completion.TrySetException(error); }
            });
            return completion.Task;
        }

        private async Task ReplayDeferredLevelingAsync(DeferredLevelingSession deferred)
        {
            bool Current()
            {
                lock (ActionLock) return ReferenceEquals(Game, deferred.Session)
                    && ReferenceEquals(_activeDeferredLeveling, deferred)
                    && (deferred.Identity == null || IsCurrentLevelingIdentity(deferred.Identity))
                    && !deferred.TerminalApplied && !deferred.Failed && !HasLevelingSynchronizationFailure;
            }
            try
            {
                if (!Current()) return;
                if (!await RunDeferredLevelingActionAsync(deferred, () =>
                {
                    InitiateLeveling(deferred.UnitId, deferred.Type, deferred.Identity);
                    // Independent remote level-ups must never open CharGen or enter a
                    // modal game mode on this peer. Replay them against a headless
                    // controller while the local player remains in the world or in
                    // their own separate level-up window.
                    LevelingInteraction.StartBackgroundLeveling(deferred.UnitId, deferred.Type);
                })) return;

                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (Current() && !LevelingInteraction.IsBackgroundLevelingReady && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50);
                }
                if (Current() && !LevelingInteraction.IsBackgroundLevelingReady)
                {
                    throw new TimeoutException("deferred-leveling-ui-timeout");
                }

                Logger.LogInformation("[LevelingOwnerUI] Replaying deferred remote level-up. Owner={Owner}, UnitId={UnitId}, Messages={Count}",
                    deferred.OwnerId, deferred.UnitId, deferred.Messages.Count);
                while (Current())
                {
                    (object Message, Action Apply) entry;
                    lock (ActionLock)
                    {
                        if (!Current() || deferred.Messages.Count == 0) break;
                        entry = deferred.Messages.Dequeue();
                    }
                    if (!await RunDeferredLevelingActionAsync(deferred, entry.Apply)) break;
                    await Task.Delay(entry.Message is NotifyLevelingPhaseChanged ? 150 : 50);
                }

                deadline = DateTime.UtcNow.AddSeconds(20);
                while (Current() && Game?.Leveling != null && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50);
                }
                if (Current() && Game?.Leveling != null)
                {
                    throw new TimeoutException("deferred-leveling-completion-timeout");
                }
            }
            catch (Exception ex)
            {
                deferred.Failed = true;
                FailLevelingSynchronization(deferred.Session, deferred.Identity, ex);
                Logger.LogError(ex, "[LevelingOwnerUI] Deferred remote level-up replay failed. Owner={Owner}, UnitId={UnitId}",
                    deferred.OwnerId, deferred.UnitId);
            }
            finally
            {
                lock (ActionLock)
                {
                    if (!deferred.Failed && deferred.Identity != null && _deferredLevelings.TryGetValue(deferred.Identity.Id, out var current)
                        && ReferenceEquals(current, deferred))
                        _deferredLevelings.Remove(deferred.Identity.Id);
                    if (ReferenceEquals(_activeDeferredLeveling, deferred))
                    {
                        _activeDeferredLeveling = null;
                        _replayingDeferredLeveling = false;
                    }
                }
                TryReplayDeferredLeveling();
            }
        }

        public bool CanMakeNewGameSequenceDecisions()
        {
            return HasControlOverUI;
        }

        public void OnLevelingWitnessPhase(NetworkLevelingPhase phase)
        {
            if (Game.Leveling == null || Game.Leveling.AwaitingHostIdentity || HasLevelingSynchronizationFailure) return;
            if (Game.Leveling.PhaseIndex != phase.Index && CanMakeLevelingDecisions())
            {
                var phaseChangedMessage = new NotifyLevelingPhaseChanged
                {
                    Phase = Mapper.Map<Networking.Messages.Contracts.NetworkLevelingPhase>(phase)
                };
                Send(phaseChangedMessage);
                Game.Leveling.PhaseIndex = phase.Index;
                ResetPlayersTracker(Game.Leveling.PlayerReadiness);
            }

            WitnessLevelingPhase(Game.LocalPlayerId);
            var message = new NotifyLevelingPhaseWitnessed
            {
                PlayerId = Game.LocalPlayerId,
                Phase = Mapper.Map<Networking.Messages.Contracts.NetworkLevelingPhase>(phase)
            };
            Send(message);
        }

        public string GetNewGameSequenceId()
        {
            return Game.Id;
        }

        public void OnNewGameSequenceWitnessPhase(NetworkNewGameSequencePhase phase)
        {
            if (Game.StartUp.PhaseType != phase.Type && CanMakeNewGameSequenceDecisions())
            {
                var phaseChangedMessage = new NotifyNewGameSequencePhaseChanged
                {
                    Phase = Mapper.Map<Networking.Messages.Contracts.NetworkNewGameSequencePhase>(phase)
                };
                Send(phaseChangedMessage);
                Game.StartUp.PhaseType = phase.Type;
                ResetPlayersTracker(Game.StartUp.ReadyPlayers);
            }

            WitnessNewGameSequencePhase(Game.LocalPlayerId, phase.Type);
            var message = new NotifyNewGameSequenceWitnessed
            {
                PlayerId = Game.LocalPlayerId,
                Phase = Mapper.Map<Networking.Messages.Contracts.NetworkNewGameSequencePhase>(phase)
            };
            Send(message);
        }

        public void OnLevelingIncreaseSkillPoint(NetworkLevelingSkillPoint skill)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingSkillPointIncreased
            {
                Skill = Mapper.Map<Networking.Messages.Contracts.NetworkLevelingSkillPoint>(skill)
            };
            Send(message);
        }

        public void OnLevelingDecreaseSkillPoint(NetworkLevelingSkillPoint skill)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingSkillPointDecreased
            {
                Skill = Mapper.Map<Networking.Messages.Contracts.NetworkLevelingSkillPoint>(skill)
            };
            Send(message);
        }

        public void OnLevelingIncreaseAbilityScore(NetworkLevelingAbilityScore abilityScore)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingAbilityScoreIncreased
            {
                AbilityScore = Mapper.Map<Networking.Messages.Contracts.NetworkLevelingAbilityScore>(abilityScore)
            };
            Send(message);
        }

        public void OnLevelingDecreaseAbilityScore(NetworkLevelingAbilityScore abilityScore)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingAbilityScoreDecreased
            {
                AbilityScore = Mapper.Map<Networking.Messages.Contracts.NetworkLevelingAbilityScore>(abilityScore)
            };
            Send(message);
        }

        public void OnLevelingPortraitSelected(NetworkLevelingPortrait levelingPortrait)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingPortraitSelected
            {
                Portrait = Mapper.Map<Networking.Messages.Contracts.NetworkLevelingPortrait>(levelingPortrait)
            };
            Send(message);
        }

        public void OnLevelingVoiceSelected(NetworkLevelingVoice levelingVoice)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingVoiceSelected
            {
                Voice = Mapper.Map<Networking.Messages.Contracts.NetworkLevelingVoice>(levelingVoice)
            };
            Send(message);
        }

        public void OnLevelingRaceSelected(string raceId)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingRaceSelected
            {
                RaceId = raceId
            };
            Send(message);
        }

        public void OnLevelingGenderSelected(string genderId)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingGenderSelected
            {
                GenderId = genderId
            };
            Send(message);
        }

        public void OnLevelingAlignmentSelected(string alignmentId)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingAlignmentSelected
            {
                AlignmentId = alignmentId
            };
            Send(message);
        }

        public void OnLevelingNameChanged(string name)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingNameChanged
            {
                Name = name
            };
            Send(message);
        }

        public void OnLevelingRacialAbilityScoreBonusChanged(NetworkLevelingSequenceDirection direction)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingRacialAbilityScoreBonusChanged
            {
                Direction = direction.ToString()
            };
            Send(message);
        }

        public void OnLevelingBirthMonthChanged(NetworkLevelingSequenceDirection direction)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingBirthMonthChanged
            {
                Direction = direction.ToString()
            };
            Send(message);
        }

        public void OnLevelingBirthDayChanged(NetworkLevelingSequenceDirection direction)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingBirthDayChanged
            {
                Direction = direction.ToString()
            };
            Send(message);
        }

        public void OnLevelingBodyTypeAppearanceChanged(int index)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingBodyTypeAppearanceChanged
            {
                Index = index
            };
            Send(message);
        }

        public void OnLevelingFaceAppearanceChanged(int index)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingFaceAppearanceChanged
            {
                Index = index
            };
            Send(message);
        }

        public void OnLevelingScarAppearanceChanged(int index)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingScarAppearanceChanged
            {
                Index = index
            };
            Send(message);
        }

        public void OnLevelingHairStyleAppearanceChanged(int index)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingHairStyleAppearanceChanged
            {
                Index = index
            };
            Send(message);
        }

        public void OnLevelingHornsAppearanceChanged(int index)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingHornsAppearanceChanged
            {
                Index = index
            };
            Send(message);
        }

        public void OnLevelingWarpaintAppearanceChanged(NetworkLevelingWarpaint levelingWarpaint)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingWarpaintAppearanceChanged
            {
                Warpaint = Mapper.Map<Networking.Messages.Contracts.NetworkLevelingWarpaint>(levelingWarpaint)
            };
            Send(message);
        }

        public void OnLevelingTattooAppearanceChanged(NetworkLevelingTattoo levelingTattoo)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingTattooAppearanceChanged
            {
                Tattoo = Mapper.Map<Networking.Messages.Contracts.NetworkLevelingTattoo>(levelingTattoo)
            };
            Send(message);
        }

        public void OnLevelingBodyColorAppearanceChanged(string textureName)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingBodyColorAppearanceChanged
            {
                TextureName = textureName
            };
            Send(message);
        }

        public void OnLevelingEyesColorAppearanceChanged(string textureName)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingEyesColorAppearanceChanged
            {
                TextureName = textureName
            };
            Send(message);
        }

        public void OnLevelingHairColorAppearanceChanged(string textureName)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingHairColorAppearanceChanged
            {
                TextureName = textureName
            };
            Send(message);
        }

        public void OnLevelingHornsColorAppearanceChanged(string textureName)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingHornsColorAppearanceChanged
            {
                TextureName = textureName
            };
            Send(message);
        }

        public void OnLevelingWarpaintColorAppearanceChanged(NetworkLevelingWarpaint levelingWarpaint)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingWarpaintColorAppearanceChanged
            {
                Warpaint = Mapper.Map<Networking.Messages.Contracts.NetworkLevelingWarpaint>(levelingWarpaint)
            };
            Send(message);
        }

        public void OnLevelingTattooColorAppearanceChanged(NetworkLevelingTattoo levelingTattoo)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingTattooColorAppearanceChanged
            {
                Tattoo = Mapper.Map<Networking.Messages.Contracts.NetworkLevelingTattoo>(levelingTattoo)
            };
            Send(message);
        }

        public void OnLevelingPrimaryOutfitColorAppearanceChanged(string textureName)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingPrimaryOutfitColorAppearanceChanged
            {
                TextureName = textureName
            };
            Send(message);
        }

        public void OnLevelingSecondaryOutfitColorAppearanceChanged(string textureName)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingSecondaryOutfitColorAppearanceChanged
            {
                TextureName = textureName
            };
            Send(message);
        }

        public void OnLevelingRespecCompleted()
        {
            ResetPlayersTracker(Game.PlayersInRespecWindow);
            var message = new NotifyLevelingRespecCompleted();
            Send(message);
        }

        public void OnLevelingRespecWindowShown(string unitId)
        {
            AddPlayerToTracker(Game.PlayersInRespecWindow, Game.LocalPlayerId);

            UpdateLevelingRespecUIState(unitId);

            var message = new NotifyLevelingRespecWindowShown
            {
                PlayerId = Game.LocalPlayerId,
                UnitId = unitId
            };
            Send(message);
        }

        public void OnLevelingRespecLevelUp()
        {
            RemovePlayerFromTracker(Game.PlayersInRespecWindow, Game.LocalPlayerId);

            var message = new NotifyLevelingRespecLevelUp
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);
        }

        public void OnLevelingRespecMythicLevelUp()
        {
            RemovePlayerFromTracker(Game.PlayersInRespecWindow, Game.LocalPlayerId);

            var message = new NotifyLevelingRespecMythicLevelUp
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);
        }

        public void OnLevelingFeatureSelected(NetworkLevelingFeature feature)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingFeatureSelected
            {
                Feature = Mapper.Map<Networking.Messages.Contracts.NetworkLevelingFeature>(feature)
            };
            Send(message);
        }

        public void OnLevelingSpellRemoved(NetworkLevelingSpell spell)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingSpellRemoved
            {
                Spell = Mapper.Map<Networking.Messages.Contracts.NetworkLevelingSpell>(spell)
            };
            Send(message);
        }

        public void OnLevelingSpellChosen(NetworkLevelingSpell spell)
        {
            if (!CanMakeLevelingDecisions())
            {
                return;
            }

            var message = new NotifyLevelingSpellChosen
            {
                Spell = Mapper.Map<Networking.Messages.Contracts.NetworkLevelingSpell>(spell)
            };
            Send(message);
        }

        public void OnLevelingTerminated()
        {
            Logger.LogInformation("Leveling has been terminated. UnitId={unitId}, Type={Type}", Game.Leveling.UnitId, Game.Leveling.Type);

            if (CanMakeLevelingDecisions())
            {
                var message = new NotifyLevelingTerminated();
                Send(message);
            }

            if (Game.Leveling.Type == NetworkLevelingType.NewGameSequence)
            {
                ResetPlayersTracker(Game.StartUp.ReadyPlayers);
            }

            var messageKey = Game.Leveling.Type switch
            {
                NetworkLevelingType.MythicLeveling => WellKnownKeys.GameNotifications.Leveling.MythicLeveling.Terminated.Key,
                NetworkLevelingType.Mercenary => WellKnownKeys.GameNotifications.Leveling.Mercenary.Terminated.Key,
                NetworkLevelingType.NewGameSequence => null,
                NetworkLevelingType.Leveling or _ => WellKnownKeys.GameNotifications.Leveling.Terminated.Key
            };

            if (!string.IsNullOrEmpty(messageKey))
            {
                PlayerNotification.AddCombatText(messageKey, CombatTextSeverity.Common, new UnitLogParameter(Game.Leveling.UnitId));
            }

            MarkDeferredLevelingTerminalApplied();
            Game.Leveling = null;
            TryReplayDeferredLeveling();
        }

        public void OnLevelingCompleted()
        {
            Logger.LogInformation("Leveling has been completed. UnitId={UnitId}, Type={Type}", Game.Leveling.UnitId, Game.Leveling.Type);

            if (CanMakeLevelingDecisions())
            {
                var message = new NotifyLevelingCompleted();
                Send(message);
            }

            var messageKey = Game.Leveling.Type switch
            {
                NetworkLevelingType.MythicLeveling => WellKnownKeys.GameNotifications.Leveling.MythicLeveling.Completed.Key,
                NetworkLevelingType.Mercenary => WellKnownKeys.GameNotifications.Leveling.Mercenary.Completed.Key,
                NetworkLevelingType.NewGameSequence or NetworkLevelingType.DungeonRestart => null,
                NetworkLevelingType.Leveling or _ => WellKnownKeys.GameNotifications.Leveling.Completed.Key,
            };

            if (!string.IsNullOrEmpty(messageKey))
            {
                PlayerNotification.AddCombatText(messageKey, CombatTextSeverity.Common, new UnitLogParameter(Game.Leveling.UnitId));
            }

            // dead -> new journey -> create new character
            if (Game.Leveling.Type == NetworkLevelingType.DungeonRestart)
            {
                UpdateCharactersOwnership();
            }

            MarkDeferredLevelingTerminalApplied();
            Game.Leveling = null;
            TryReplayDeferredLeveling();
        }

        private void MarkDeferredLevelingTerminalApplied()
        {
            lock (ActionLock)
            {
                var identity = Game.Leveling?.Identity;
                if (identity != null && _deferredLevelings.TryGetValue(identity.Id, out var deferred)
                    && ReferenceEquals(deferred.Session, Game) && SameLevelingIdentity(deferred.Identity, identity))
                    deferred.TerminalApplied = true;
            }
        }

        public void OnCharacterSelectionWindowShown()
        {
            AddPlayerToTracker(Game.PlayersInCharacterSelectionWindow, Game.LocalPlayerId);

            UpdateCharacterSelectionUIState();

            var message = new NotifyCharacterSelectionWindowShown
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);
        }

        public void OnMoveActionBarSlot(NetworkActionBarSlot sourceActionBarSlot, NetworkActionBarSlot targetActionBarSlot)
        {
            if (!IsControlledByLocalPlayer(sourceActionBarSlot.UnitId))
            {
                return;
            }

            var message = new NotifyActionBarSlotMoved
            {
                SourceActionBarSlot = Mapper.Map<Networking.Messages.Contracts.NetworkActionBarSlot>(sourceActionBarSlot),
                TargetActionBarSlot = Mapper.Map<Networking.Messages.Contracts.NetworkActionBarSlot>(targetActionBarSlot),
            };
            Send(message);
        }

        public void OnClearActionBarSlot(NetworkActionBarSlot actionBarSlot)
        {
            if (!IsControlledByLocalPlayer(actionBarSlot.UnitId))
            {
                return;
            }

            var message = new NotifyActionBarSlotCleared
            {
                ActionBarSlot = Mapper.Map<Networking.Messages.Contracts.NetworkActionBarSlot>(actionBarSlot)
            };
            Send(message);
        }

        public void OnLockpickInteraction(NetworkLockpickInteraction lockpickInteraction)
        {
            var message = new NotifyMapObjectLockpicked
            {
                LockpickInteraction = Mapper.Map<Networking.Messages.Contracts.NetworkLockpickInteraction>(lockpickInteraction)
            };
            Send(message);
        }

        public void OnSetUnitStealthEnabled(string unitId, bool isEnabled, bool isForced)
        {
            var message = new NotifyUnitStealthChanged
            {
                UnitId = unitId,
                IsEnabled = isEnabled,
                IsForced = isForced
            };
            Send(message);
        }

        public void OnGlobalMapMessageBoxShown(bool shouldManuallyTriggerMessage)
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapLocationMessage, Game.LocalPlayerId);

            var viewModel = Main.UIAccessor.GlobalMapEnterMessagePCView?.ViewModel;
            var arrivalPoint = viewModel?.IsCurrentLocation == true
                && Kingmaker.Game.Instance.GlobalMapController.SelectedTraveler is Kingmaker.Globalmap.State.GlobalMapPlayerState
                ? viewModel.Location?.Blueprint : null;
            var message = new NotifyGlobalMapLocationMessageShown
            {
                PlayerId = Game.LocalPlayerId,
                ShouldManuallyTriggerMessage = shouldManuallyTriggerMessage,
                Scope = GlobalMapPresentationScope.Current,
                Location = arrivalPoint == null ? null : new Networking.Messages.Contracts.NetworkGlobalMapLocation
                    { Id = arrivalPoint.AssetGuid.ToString(), Name = arrivalPoint.name }
            };
            Send(message);

            UpdateGlobalMapLocationMessageUIState();
        }

        public void OnShowGroupChangerUI()
        {
            AddPlayerToTracker(Game.PlayersInGroupChanger, Game.LocalPlayerId);

            var message = new NotifyGroupChangerOpened
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);

            UpdateGroupManagerUIState();
        }

        public void OnSkipTimeOpened()
        {
            AddPlayerToTracker(Game.PlayersInSkipTime, Game.LocalPlayerId);

            var message = new NotifySkipTimeOpened
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);

            UpdateSkipTimeUIState();
        }

        public void OnDialogPopupShown(NetworkDialogPopup networkDialogPopup)
        {
            AddPlayerToTracker(Game.PlayersInDialogPopup, Game.LocalPlayerId);

            var message = new NotifyDialogPopupShown
            {
                PlayerId = Game.LocalPlayerId,
                Popup = Mapper.Map<Networking.Messages.Contracts.NetworkDialogPopup>(networkDialogPopup)
            };
            Send(message);

            UpdateDialogPopupState();
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

        public void OnGlobalMapCommonPopupShown(NetworkGlobalMapCommonPopup globalMapCommonPopup)
        {
            GlobalMapInteraction.ObserveCommonPopup(globalMapCommonPopup);
            AddPlayerToTracker(Game.PlayersInGlobalMapCommonPopup, Game.LocalPlayerId);

            var message = new NotifyGlobalMapCommonPopupShown
            {
                PlayerId = Game.LocalPlayerId,
                Popup = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapCommonPopup>(globalMapCommonPopup),
                Scope = GlobalMapPresentationScope.Current
            };
            Send(message);

            UpdateGlobalMapCommonPopupUIState(globalMapCommonPopup);
        }

        public void OnGlobalMapEncounterMessageShown()
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapEncounterMessage, Game.LocalPlayerId);

            var message = new NotifyGlobalMapEncounterMessageShown
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);

            UpdateGlobalMapEncounterMessageUIState();
        }

        public void OnGlobalMapDisposed()
        {
            ResetGlobalMapCounters();
        }

        public void OnGlobalMapTravelerModeChanged(NetworkGlobalMapTravelerMode travelerMode)
        {
            RegisterGlobalMapMode(Game.LocalPlayerId, travelerMode);
            var message = new NotifyGlobalMapTravelerModeChanged
            {
                PlayerId = Game.LocalPlayerId,
                TravelerMode = travelerMode.ToString(),
                MustBeEnforced = HasControlOverUI
            };
            Send(message);
            UpdateGlobalMapUIState();
        }

        public void OnUnitDeath(string unitId, string groupId)
        {
            if (Game.Combat != null)
            {
                OnUnitDeathInCombat(unitId, groupId);
            }
            else if (Game.ArmyCombat != null)
            {
                OnUnitDeathInTacticalCombat(unitId);
            }
        }

        public void OnTrapDisarmRolled(NetworkTrapDisarm trapDisarm)
        {
            var message = new NotifyTrapDisarmRolled
            {
                TrapDisarm = Mapper.Map<Networking.Messages.Contracts.NetworkTrapDisarm>(trapDisarm)
            };
            Send(message);
        }

        public void OnTrapActivation(string unitId, NetworkMapObject trapObject)
        {
            var message = new NotifyTrapActivated
            {
                UnitId = unitId,
                Trap = Mapper.Map<Networking.Messages.Contracts.NetworkMapObject>(trapObject)
            };
            Send(message);
        }

        public void OnUnitAutoUseAbilityChanged(NetworkAutoUseAbility networkAutoUseAbility)
        {
            if (!IsControlledByLocalPlayer(networkAutoUseAbility.UnitId))
            {
                return;
            }

            var message = new NotifyUnitAutoUseAbilityChanged
            {
                AutoUse = Mapper.Map<Networking.Messages.Contracts.NetworkAutoUseAbility>(networkAutoUseAbility)
            };
            Send(message);
        }

        public void OnUnitLootUnit(NetworkUnitLootUnit networkUnitLootUnit)
        {
            if (!IsControlledByLocalPlayer(networkUnitLootUnit.InitiatorUnitId))
            {
                return;
            }

            var message = new NotifyUnitLootedUnit
            {
                LootUnit = Mapper.Map<Networking.Messages.Contracts.NetworkUnitLootUnit>(networkUnitLootUnit)
            };
            Send(message);
        }

        public void OnMapObjectCombinePartInteraction(NetworkMapObject mapObject, string interactedUnitId, int partIndex)
        {
            var message = new NotifyMapObjectCombinePartInteracted
            {
                MapObject = Mapper.Map<Networking.Messages.Contracts.NetworkMapObject>(mapObject),
                UnitId = interactedUnitId,
                PartIndex = partIndex
            };
            Send(message);
        }

        public void OnUnitInteractWithUnit(NetworkUnitInteractWithUnit networkUnitInteractWithUnit)
        {
            if (!IsControlledByLocalPlayer(networkUnitInteractWithUnit.InitiatorUnitId))
            {
                return;
            }

            var message = new NotifyUnitInteractedWithUnit
            {
                Interaction = Mapper.Map<Networking.Messages.Contracts.NetworkUnitInteractWithUnit>(networkUnitInteractWithUnit)
            };
            Send(message);
        }

        public void OnUnitMoveTo(NetworkUnitMoveTo unitMoveTo)
        {
            if (!IsControlledByLocalPlayer(unitMoveTo.InitiatorUnitId))
            {
                return;
            }

            StampTurnMovement(unitMoveTo);
            var message = new NotifyUnitMovedTo
            {
                Movement = Mapper.Map<Networking.Messages.Contracts.NetworkUnitMoveTo>(unitMoveTo)
            };
            Send(message);
        }

        public void CleanupUntargetableUnitsState()
        {
            var groups = Game.Combat?.UntargetableUnits.ToList() ?? [];
            foreach (var group in groups)
            {
                var units = group.Value.ToList();
                foreach (var unit in units)
                {
                    MakeUnitTargetable(unit, group.Key);
                }
            }
        }

        public void OnItemDescriptionRead(NetworkItem networkItem)
        {
            var message = new NotifyItemDescriptionRead
            {
                Item = Mapper.Map<Networking.Messages.Contracts.NetworkItem>(networkItem)
            };
            Send(message);
        }

        public void OnCopyInventoryItem(NetworkItemCopy itemCopy)
        {
            var message = new NotifyInventoryItemCopied
            {
                Copy = Mapper.Map<Networking.Messages.Contracts.NetworkItemCopy>(itemCopy)
            };
            Send(message);
        }

        public void OnZoneLootCollectorButtonsUpdated()
        {
            UpdateZoneLootUIState();
        }

        public void OnZoneLootShown(string areaExitId)
        {
            AddPlayerToTracker(Game.PlayersInZoneLoot, Game.LocalPlayerId);

            var message = new NotifyZoneLootShown
            {
                PlayerId = Game.LocalPlayerId,
                AreaExitId = areaExitId
            };
            Send(message);

            UpdateZoneLootUIState();
        }

        public void OnZoneLootClosed()
        {
            RemovePlayerFromTracker(Game.PlayersInZoneLoot, Game.LocalPlayerId);

            var message = new NotifyZoneLootClosed
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);
        }

        public bool ReadyChanged()
        {
            var player = Game.Players.First(p => p.Id == Game.LocalPlayerId);
            return ReadyChanged(player, !player.IsReady);
        }

        public void OnCrusadeArmyBattleResultsShown()
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapCrusadeArmyBattleResults, Game.LocalPlayerId);

            var message = new NotifyCrusadeArmyBattleResultsShown
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);

            UpdateGlobalMapCrusadeArmyBattleResultsUIState();
        }

        public void OnCrusadeArmyBattleResultsClosed()
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmyBattleResults);
            var message = new NotifyCrusadeArmyBattleResultsClosed();
            Send(message);
        }

        public void OnGlobalMapCombatResultsShown()
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapCombatResults, Game.LocalPlayerId);

            var message = new NotifyGlobalMapCombatResultsShown
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);

            UpdateGlobalMapCombatResultsUIState();
        }

        public void OnGlobalMapCrusadeArmyInfoShown()
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapCrusadeArmyInfo, Game.LocalPlayerId);

            var message = new NotifyGlobalMapCrusadeArmyInfoShown
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);

            UpdateGlobalMapCrusadeArmyInfoUIState();
        }

        public void OnGlobalMapCrusadeArmyMergeCartClosed()
        {
            RemovePlayerFromTracker(Game.PlayersInGlobalMapCrusadeArmyInfoMerge, Game.LocalPlayerId);

            var message = new NotifyGlobalMapCrusadeArmyMergeCartClosed
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);

            UpdateGlobalMapCrusadeArmyInfoUIStateAfterMerge();
        }

        public void OnGlobalMapCrusadeArmyInfoMergeShown()
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapCrusadeArmyInfoMerge, Game.LocalPlayerId);

            var message = new NotifyGlobalMapCrusadeArmyMergeCartShown
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);

            UpdateGlobalMapCrusadeArmyInfoUIStateOnMerge();
        }

        public void OnGlobalMapCrusadeArmySetLeaderShown()
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapCrusadeArmySetLeader, Game.LocalPlayerId);

            var message = new NotifyGlobalMapCrusadeArmySetLeaderShown
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);

            UpdateGlobalMapCrusadeArmyInfoUIStateOnSetLeader();
        }

        public void OnGlobalMapCrusadeArmySetLeaderClosed()
        {
            RemovePlayerFromTracker(Game.PlayersInGlobalMapCrusadeArmySetLeader, Game.LocalPlayerId);

            var message = new NotifyGlobalMapCrusadeArmySetLeaderClosed
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);

            UpdateGlobalMapCrusadeArmyInfoUIStateAfterSetLeader();
        }

        public void OnGlobalMapCrusadeArmyBuyLeaderShown()
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapCrusadeArmyBuyLeader, Game.LocalPlayerId);

            var message = new NotifyGlobalMapCrusadeArmyBuyLeaderShown
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);

            UpdateGlobalMapCrusadeArmyBuyLeaderUIState();
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

        public bool OnBeforeChooseBookCharacter(string cueName, string answerName)
        {
            // no need to check for cue witnesses here as it's only a popup for character selection window
            if (HasControlOverFeature(NetworkPlayerControlledFeature.Dialogs))
            {
                return true;
            }

            MakeCueAnswerSuggestion(cueName, answerName);

            return false;
        }

        public virtual void MakeCueAnswerSuggestion(string cueName, string answerName)
        {
        }

        public void ForceUnpause()
        {
            if (IsCombatStartupBlocked) { GameInteraction.SetPause(true); return; }
            lock (ActionLock)
            {
                Game.ForcedPause = null;
                GameInteraction.SetPause(false);
            }
        }

        public bool ToggleManualPause(bool isPaused)
        {
            if (IsCombatStartupBlocked)
            {
                GameInteraction.SetPause(true);
                // The keyboard caller toggles IsPaused again when this returns
                // true. Safety pause has already been applied locally.
                return false;
            }
            lock (ActionLock)
            {
                // unpaused => paused
                if (!isPaused)
                {
                    if (IsPauseOnCooldown(NetworkForcedPauseReason.Manual))
                    {
                        return false;
                    }

                    EnsureForcePaused(NetworkForcedPauseReason.Manual, removalDelay: null);
                    Game.ForcedPause.ReadyPlayers.Add(Game.LocalPlayerId);

                    var pauseStarted = new NotifyGamePauseStarted
                    {
                        PlayerId = Game.LocalPlayerId,
                        Pause = Mapper.Map<Networking.Messages.Contracts.NetworkForcedPause>(Game.ForcedPause)
                    };
                    Send(pauseStarted);

                    return true;
                }

                // paused => unpaused
                var canContinue = OnToggleOffPause(out var showReason);
                if (canContinue)
                {
                    return true;
                }

                if (showReason)
                {
                    ShowForcedPauseReason();
                }

                return false;
            }
        }

        public void OnGlobalMapCrusadeArmyBuyLeaderClosed()
        {
            RemovePlayerFromTracker(Game.PlayersInGlobalMapCrusadeArmyBuyLeader, Game.LocalPlayerId);

            var message = new NotifyGlobalMapCrusadeArmyBuyLeaderClosed
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);

            UpdateGlobalMapCrusadeArmyInfoUIStateAfterBuyLeader();
        }

        public void OnGlobalMapRecruitmentShown()
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapRecruitment, Game.LocalPlayerId);

            var message = new NotifyGlobalMapRecruitmentShown
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);

            UpdateGlobalMapRecruitmentUIState();
        }

        public void OnGlobalMapRecruitmentClosed()
        {
            RemovePlayerFromTracker(Game.PlayersInGlobalMapRecruitment, Game.LocalPlayerId);

            var message = new NotifyGlobalMapRecruitmentClosed
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);
        }

        public void OnGlobalMapRecruitmentSlotsRerolled()
        {
            UpdateGlobalMapRecruitmentUIState();
        }

        public void OnGlobalMapCrusadeArmyLeaderLevelingStarted(NetworkGlobalMapArmy globalMapArmy)
        {
            var message = new NotifyGlobalMapCrusadeArmyLeaderLevelingStarted
            {
                Army = Mapper.Map<Networking.Messages.Contracts.NetworkGlobalMapArmy>(globalMapArmy)
            };
            Send(message);
        }

        public void OnGlobalMapCrusadeArmyLeaderLevelingShown()
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapCrusadeArmyLeaderLeveling, Game.LocalPlayerId);

            var message = new NotifyGlobalMapCrusadeArmyLeaderLevelingShown
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);

            UpdateGlobalMapCrusadeArmyLeaderLevelingUIState();
        }

        public void OnTacticalCombatEnded()
        {
            Game.ArmyCombat = null;
            Logger.LogInformation("Crusade army combat has ended");
            ValueGenerator.ResetSeededGenerators(IdentifierLifetime.Combat, IdentifierLifetime.CombatTurn);
        }

        public bool OnBeforeTacticalCombatTurnStart(int turnNumber)
        {
            if (Game.ArmyCombat.Turn != null && Game.ArmyCombat.Turn.LockCounter > 0)
            {
                return false;
            }

            var playerId = Game.LocalPlayerId;
            if (AddPlayerCrusadeArmyCombatTurnInitialization(turnNumber, playerId))
            {
                if (turnNumber == 0)
                {
                    Game.ArmyCombat.Turn = new NetworkArmyCombatTurn
                    {
                        Number = turnNumber,
                        IsSyncRequired = true, // seems to be a bit redundant to sync on turn 0
                    };
                }

                var message = new NotifyTacticalCombatTurnInitialized
                {
                    PlayerId = playerId,
                    TurnNumber = turnNumber,
                };
                Send(message);
            }

            var isTurnInitialized = IsCrusadeArmyCombatTurnInitialized();
            if (!isTurnInitialized)
            {
                return false;
            }

            var isTurnSynced = IsArmyCombatTurnSynced();
            if (isTurnSynced)
            {
                // High morale proc keeps the same turn
                Game.ArmyCombat.Turn.IsSyncRequired = true;
                Game.ArmyCombat.Turn.SyncedPlayers.Clear();
            }

            return isTurnSynced;
        }

        public void OnCrusadeArmyCombatTurnStarted(NetworkArmyCombatTurn armyCombatTurn)
        {
            Game.ArmyCombat.Turn = armyCombatTurn;
            Logger.LogInformation("Crusade Army combat turn has been started. TurnNumber={TurnNumber}, UnitId={UnitId}, IsAI={IsAI}", Game.ArmyCombat.Turn.Number, Game.ArmyCombat.Turn.UnitId, Game.ArmyCombat.Turn.IsAI);
        }

        public void OnEnterKingdom(NetworkKingdomEntryPoint kingdomEntryPoint)
        {
            var message = new NotifyKingdomEntered
            {
                EntryPoint = Mapper.Map<Networking.Messages.Contracts.NetworkKingdomEntryPoint>(kingdomEntryPoint)
            };
            Send(message);
        }

        public void OnExitKingdom()
        {
            var message = new NotifyKingdomExited();
            Send(message);
        }

        public void OnKingdomLoaded()
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapKingdom, Game.LocalPlayerId);
            var message = new NotifyKingdomLoaded
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);
            UpdateGlobalMapKingdomUIState();
        }

        public void OnKingdomUnloaded()
        {
            var message = new NotifyKingdomUnloaded
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);
            ResetPlayersTracker(Game.PlayersInGlobalMapKingdom);
            ResetPlayersTracker(Game.PlayersInSettlement);
            Game.PlayersInKingdomNavigationType.Clear();
        }

        public void OnKingdomNavigationChanged(KingdomNavigationType kingdomNavigationType)
        {
            var message = new NotifyKingdomNavigationChanged
            {
                Type = kingdomNavigationType.ToString(),
                PlayerId = Game.LocalPlayerId
            };
            Send(message);
        }

        public void OnKingdomSettlementLoaded()
        {
            AddPlayerToTracker(Game.PlayersInSettlement, Game.LocalPlayerId);
            var message = new NotifyKingdomSettlementLoaded
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);
            UpdateSettlementUIState();
        }

        public void OnTransitionMapShown()
        {
            AddPlayerToTracker(Game.PlayersInTransitionMap, Game.LocalPlayerId);
            var message = new NotifyTransitionMapShown
            {
                PlayerId = Game.LocalPlayerId
            };
            Send(message);
            UpdateTransitionMapUIState();
        }

        protected abstract bool IsArmyCombatTurnSynced();

        protected abstract void OnLocalPlayerTurnStart();

        protected abstract void Send(object message);

        protected abstract void Send(long playerId, object message);

        protected abstract bool OnToggleOffPause(out bool showReason);

        protected abstract void OnLocalPlayerTurnEnd();

        protected void OnTurnEnded(string unitId)
        {
            Logger.LogInformation("Turn end is allowed. Round={Round}, TurnUnitId={TurnUnitId}, IsAI={IsAI}, UnitId={UnitId}", Game.Combat.Round, Game.Combat.Turn.UnitId, Game.Combat.Turn.IsAI, unitId);
            Game.Combat.TriggeredAreaEffects.Clear();
            ResetCombatTurn();
            PlayerNotification.AddCombatText(WellKnownKeys.GameNotifications.Combat.Turn.Ended.Key, CombatTextSeverity.Common, new UnitLogParameter(unitId));
            var confirmedUnits = Game.Combat.ConfirmedMidCombatUnits.ToList() ?? [];
            if (confirmedUnits.Count > 0)
            {
                CombatInteraction.AddUnitsToCombat(confirmedUnits);
            }
        }

        protected void SetPauseCooldown()
        {
            lock (ActionLock)
            {
                if (Game.ForcedPause == null)
                {
                    return;
                }

                if (Game.ForcedPause.Reason == NetworkForcedPauseReason.Manual || Game.ForcedPause.Reason == NetworkForcedPauseReason.TrapDetected)
                {
                    Logger.LogInformation("Removing pause as combat has started for one or more players. PauseReason={PauseReason}", Game.ForcedPause.Reason);
                    ForceUnpause();

                    if (_pauseCooldown != null)
                    {
                        return;
                    }

                    var settings = SettingsService.GetSettings();
                    if (settings.CombatPauseCooldown == TimeSpan.Zero)
                    {
                        Logger.LogWarning("Combat pause cooldown is disabled");
                        return;
                    }

                    _pauseCooldown = DateTime.UtcNow.Add(settings.CombatPauseCooldown);
                    Task.Delay(settings.CombatPauseCooldown).ContinueWith(_ =>
                    {
                        lock (ActionLock)
                        {
                            _pauseCooldown = null;
                        }
                    });
                }
            }
        }

        protected bool IsPauseOnCooldown(NetworkForcedPauseReason pauseReason)
        {
            lock (ActionLock)
            {
                if (_pauseCooldown == null)
                {
                    return false;
                }

                var currentTime = DateTime.UtcNow;
                if (currentTime < _pauseCooldown.Value)
                {
                    Logger.LogWarning("Pause is on cooldown. CurrentTime={CurrentTime}, BanRemoval={BanRemoval}, PauseReason={PauseReason}", currentTime, _pauseCooldown, pauseReason);
                    return true;
                }

                return false;
            }
        }

        protected void ShowForcedPauseReason()
        {
            var pause = Game.ForcedPause;
            if (pause == null)
            {
                return;
            }

            var messageKey = pause.IsLifting ? WellKnownKeys.GameNotifications.ForcedPause.IsLifting.Key : pause.Reason switch
            {
                NetworkForcedPauseReason.Manual => WellKnownKeys.GameNotifications.ForcedPause.ManualPause.Key,
                NetworkForcedPauseReason.AreaLoading => WellKnownKeys.GameNotifications.ForcedPause.AreaLoading.Key,
                NetworkForcedPauseReason.RestEncounterLoading => WellKnownKeys.GameNotifications.ForcedPause.RestRandomEncounterLoading.Key,
                NetworkForcedPauseReason.TrapDetected => WellKnownKeys.GameNotifications.ForcedPause.TrapDetected.Key,
                _ => null
            };

            if (!string.IsNullOrEmpty(messageKey))
            {
                PlayerNotification.ShowWarningNotification(messageKey);
            }
        }

        protected bool AddPlayerCrusadeArmyCombatTurnInitialization(int turnNumber, long playerId)
        {
            var isFirstAdd = true;
            Game.ArmyCombat.PlayersNextTurnInitialization.AddOrUpdate(turnNumber, [playerId], (key, existing) =>
            {
                isFirstAdd = existing.Add(playerId);
                return existing;
            });

            if (isFirstAdd)
            {
                Logger.LogInformation("Crusade Army Turn initialization has been confirmed. PlayerId={PlayerId}, TurnNumber={TurnNumber}", playerId, turnNumber);
            }

            return isFirstAdd;
        }

        protected bool IsCrusadeArmyCombatTurnInitialized()
        {
            if (Game.ArmyCombat?.Turn == null
                || !Game.ArmyCombat.IsInitialized
                || !Game.ArmyCombat.PlayersNextTurnInitialization.TryGetValue(Game.ArmyCombat.Turn.Number, out var readyPlayers))
            {
                return false;
            }

            var isReady = readyPlayers.Count >= GetSyncedPlayersCount();
            return isReady;
        }

        protected virtual void OnLocalCrusadeArmyCombatTurnInitialized(int turnNumber, long playerId)
        {
        }

        protected virtual void OnLocalRestGameModeEnded()
        {
            if (Game.ForcedPause != null)
            {
                GameInteraction.SetPause(true);
            }
        }

        protected virtual void OnRemoteRestGameModeEnded(long playerId)
        {
            UpdateRestUIState();
        }

        protected virtual void OnRemoteRestGameModeStarted()
        {
            UpdateRestUIState();
        }

        protected virtual void OnRemotePauseGameModeStarted(long playerId)
        {
            lock (ActionLock)
            {
                Game.ForcedPause?.ReadyPlayers.Add(playerId);
            }
        }

        protected void RegisterGlobalMapMode(long playerId, NetworkGlobalMapTravelerMode travelerMode)
        {
            Game.PlayersInGlobalMapMode.AddOrUpdate(playerId, travelerMode, (key, existing) => travelerMode);
        }

        protected void InitiateLeveling(string unitId, NetworkLevelingType levelingType,
            NetworkLevelingIdentity identity = null)
        {
            if (Game.Leveling != null)
            {
                Logger.LogWarning("Previous leveling has not been finished. UnitId={UnitId}, Type={Type}", Game.Leveling.UnitId, Game.Leveling.Type);
            }

            Game.Leveling = new NetworkLeveling(unitId, levelingType)
            {
                Identity = identity ?? CreateLevelingIdentity(Game.LocalPlayerId, unitId, levelingType)
            };
        }

        protected async Task WaitForTurnToBeCompletedAsync()
        {
            // making sure all previous actions are finished
            await WaitWhileTrue(() => CombatInteraction.IsRiderActive() || Game.Combat.Turn.LockCounter > 0, "Waiting for all combat commands to finish before ending turn");
            var settings = SettingsService.GetSettings();
            if (settings.PlayerTurnEndDelay > TimeSpan.Zero)
            {
                Logger.LogInformation("Waiting for artificial delay before ending player turn. Delay={Delay}", settings.PlayerTurnEndDelay);
                await Task.Delay(settings.PlayerTurnEndDelay);
            }
        }

        protected void UpdateGroupManagerUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInGroupChanger.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GameInteraction.UpdateGroupChangerUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateDialogPopupState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInDialogPopup.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverFeature(NetworkPlayerControlledFeature.Dialogs) && readyPlayers >= totalPlayers;
                DialogInteraction.UpdateDialogPopupUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateSkipTimeUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInSkipTime.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GameInteraction.UpdateSkipTimeUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateGlobalMapLocationMessageUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInGlobalMapLocationMessage.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GlobalMapInteraction.UpdateLocationMessageBoxUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateGlobalMapCommonPopupUIState(NetworkGlobalMapCommonPopup globalMapCommonPopup)
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInGlobalMapCommonPopup.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GlobalMapInteraction.UpdateCommonPopupUI(globalMapCommonPopup, canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateGlobalMapCombatResultsUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInGlobalMapCombatResults.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GlobalMapInteraction.UpdateCombatResultsUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateGlobalMapCrusadeArmyInfoUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInGlobalMapCrusadeArmyInfo.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GlobalMapInteraction.UpdateCrusadeArmyInfoUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateGlobalMapCrusadeArmyInfoUIStateOnMerge()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInGlobalMapCrusadeArmyInfoMerge.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GlobalMapInteraction.UpdateCrusadeArmyInfoUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateGlobalMapCrusadeArmyInfoUIStateOnSetLeader()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInGlobalMapCrusadeArmySetLeader.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GlobalMapInteraction.UpdateSharedCrusadeManagementUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateGlobalMapRecruitmentUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInGlobalMapRecruitment.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GlobalMapInteraction.UpdateRecruitmentUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateGlobalMapCrusadeArmyLeaderLevelingUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInGlobalMapCrusadeArmyLeaderLeveling.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GlobalMapInteraction.UpdateLeaderLevelingUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateGlobalMapCrusadeArmyBuyLeaderUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInGlobalMapCrusadeArmyBuyLeader.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GlobalMapInteraction.UpdateBuyLeaderUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateGlobalMapCrusadeArmyInfoUIStateAfterMerge()
        {
            lock (ActionLock)
            {
                var totalPlayers = GetSyncedPlayersCount();
                var readyPlayers = totalPlayers - Game.PlayersInGlobalMapCrusadeArmyInfoMerge.Count;
                var canUse = HasControlOverUI && Game.PlayersInGlobalMapCrusadeArmyInfoMerge.Count == 0;
                GlobalMapInteraction.UpdateCrusadeArmyInfoUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateGlobalMapCrusadeArmyInfoUIStateAfterBuyLeader()
        {
            lock (ActionLock)
            {
                var totalPlayers = GetSyncedPlayersCount();
                var readyPlayers = totalPlayers - Game.PlayersInGlobalMapCrusadeArmyBuyLeader.Count;
                var canUse = HasControlOverUI && Game.PlayersInGlobalMapCrusadeArmyBuyLeader.Count == 0;
                GlobalMapInteraction.UpdateSharedCrusadeManagementUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateGlobalMapCrusadeArmyInfoUIStateAfterSetLeader()
        {
            lock (ActionLock)
            {
                var totalPlayers = GetSyncedPlayersCount();
                var readyPlayers = totalPlayers - Game.PlayersInGlobalMapCrusadeArmySetLeader.Count;
                var canUse = HasControlOverUI && Game.PlayersInGlobalMapCrusadeArmySetLeader.Count == 0;
                GlobalMapInteraction.UpdateSharedCrusadeManagementUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateGlobalMapCrusadeArmyBattleResultsUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInGlobalMapCrusadeArmyBattleResults.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var feature = CombatInteraction.IsInCrusadeTacticalCombat() ? NetworkPlayerControlledFeature.CrusadeArmyCombat : NetworkPlayerControlledFeature.GlobalMap;
                var canUse = HasControlOverFeature(feature) && readyPlayers >= totalPlayers;
                GlobalMapInteraction.UpdateCrusadeArmyBattleResultsUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateGlobalMapEncounterMessageUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInGlobalMapEncounterMessage.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GlobalMapInteraction.UpdateEncounterMessageUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateZoneLootUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInZoneLoot.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GameInteraction.UpdateZoneLootUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateLevelingRespecUIState(string unitId)
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInRespecWindow.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = WasControlledByCurrentPlayer(unitId) && readyPlayers >= totalPlayers;
                LevelingInteraction.UpdateLevelingRespecUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateCharacterSelectionUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInCharacterSelectionWindow.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GameInteraction.UpdateCharacterSelectionUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateRestUIState()
        {
            lock (ActionLock)
            {
                Game.PlayersInGameMode.TryGetValue(GameModeType.Rest, out var restReadyPlayers);
                var readyPlayersCount = (restReadyPlayers ?? []).Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayersCount >= totalPlayers;
                GameInteraction.UpdateRestUI(canUse, readyPlayersCount, totalPlayers);
            }
        }

        protected void UpdateRestResultsUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.Rest.PlayersFinishedRest.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GameInteraction.UpdateRestUI(canUse, readyPlayers, totalPlayers);
            }
        }

        protected int? GetPlayersCountWithSyncedGlobalMapMode()
        {
            lock (ActionLock)
            {
                if (!Game.PlayersInGlobalMapMode.TryGetValue(Game.LocalPlayerId, out var localPlayerMode))
                {
                    Logger.LogWarning("Global map mode for local player is not set");
                    return null;
                }

                var readyPlayers = Game.PlayersInGlobalMapMode.Count(x => x.Value == localPlayerMode);
                return readyPlayers;
            }
        }

        protected void UpdateGlobalMapUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = GetPlayersCountWithSyncedGlobalMapMode();
                if (!readyPlayers.HasValue)
                {
                    Logger.LogWarning("Unable to update global map ui state due to invalid ready players count");
                    return;
                }

                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers.Value >= totalPlayers;
                GlobalMapInteraction.UpdateUIState(canUse, readyPlayers.Value, totalPlayers);
            }
        }

        protected void UpdateGlobalMapKingdomUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInGlobalMapKingdom.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GlobalMapInteraction.UpdateKingdomUIState(canUse, readyPlayers, totalPlayers);
            }
        }


        protected void UpdateDungeonBoonSelectorUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInDungeonBoonSelector.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GameInteraction.UpdateDungeonBoonUIState(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateDungeonGameOverUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInDungeonGameOver.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GameInteraction.UpdateDungeonGameOverUIState(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateSettlementUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInSettlement.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GlobalMapInteraction.UpdateSettlementUIState(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void UpdateTransitionMapUIState()
        {
            lock (ActionLock)
            {
                var readyPlayers = Game.PlayersInTransitionMap.Count;
                var totalPlayers = GetSyncedPlayersCount();
                var canUse = HasControlOverUI && readyPlayers >= totalPlayers;
                GameInteraction.UpdateTransitionMapUIState(canUse, readyPlayers, totalPlayers);
            }
        }

        protected void ResetPlayersTracker(HashSet<long> tracker)
        {
            lock (ActionLock)
            {
                tracker.Clear();
            }
        }

        protected void AddPlayerToTracker(HashSet<long> tracker, long playerId)
        {
            lock (ActionLock)
            {
                tracker.Add(playerId);
            }
        }

        protected void RemovePlayerFromTracker(HashSet<long> tracker, long playerId)
        {
            lock (ActionLock)
            {
                tracker.Remove(playerId);
            }
        }

        protected void SetLobbyStage(NetworkLobbyStage lobbyStage)
        {
            Game.Stage = lobbyStage;
            Logger.LogInformation("Lobby stage has been changed. Stage={Stage}", lobbyStage);
        }

        protected void EnsureForcePaused(NetworkForcedPauseReason reason, TimeSpan? removalDelay)
        {
            Game.ForcedPause ??= new NetworkForcedPause
            {
                Reason = reason,
                RemovalDelay = removalDelay,
            };
            Logger.LogInformation("Forced pause has been initialized. Reason={Reason}, RemovalDelay={RemovalDelay}", reason, removalDelay);
        }

        protected void EnsureForcePaused(NetworkForcedPauseReason reason)
        {
            EnsureForcePaused(reason, SettingsService.GetSettings().ForcedPauseDefaultTerminationDelay);
        }

        protected void ResetManualPause()
        {
            if (Game.ForcedPause != null && Game.ForcedPause.Reason == NetworkForcedPauseReason.Manual)
            {
                Game.ForcedPause = null;
            }
        }

        protected void WitnessLevelingPhase(long playerId)
        {
            lock (ActionLock)
            {
                Game.Leveling.PlayerReadiness.Add(playerId);

                var isEnabled = CanMakeLevelingDecisions();
                LevelingInteraction.UpdateLevelingPhaseControls(isEnabled);
            }
        }

        protected void WitnessNewGameSequencePhase(long playerId, NetworkNewGameSequencePhaseType newGameSequencePhaseType)
        {
            lock (ActionLock)
            {
                Game.StartUp.ReadyPlayers.Add(playerId);

                var isEnabled = HasControlOverUI && Game.StartUp.ReadyPlayers.Count >= GetSyncedPlayersCount();
                GameInteraction.UpdateNewGameSequencePhaseControls(isEnabled, newGameSequencePhaseType);
            }
        }

        protected bool RegisterGameMode(GameModeType type, long playerId)
        {
            var isNew = true;
            Game.PlayersInGameMode.AddOrUpdate(type,
                k => new HashSet<long>([playerId]),
                (k, existing) =>
                {
                    // side effects are fine
                    isNew = existing.Add(playerId);
                    return existing;
                });

            return isNew;
        }

        protected bool UnregisterGameMode(GameModeType type, long playerId)
        {
            if (Game.PlayersInGameMode.TryGetValue(type, out var players))
            {
                return players.Remove(playerId);
            }

            return false;
        }

        protected void ShowPlayerConnectedMessage(NetworkPlayer networkPlayer)
        {
            if (Game.Stage != NetworkLobbyStage.Playing)
            {
                return;
            }

            PlayerNotification.ShowWarningNotification(WellKnownKeys.GameNotifications.Session.PlayerJoinedShort.Key, addToLog: false, args: networkPlayer.Name);
            PlayerNotification.AddCombatText(WellKnownKeys.GameNotifications.Session.PlayerJoinedFull.Key, CombatTextSeverity.Common, args: new PlayerLogParameter(networkPlayer));
        }

        protected void ShowPlayerDisconnectedMessage(NetworkPlayer networkPlayer)
        {
            if (networkPlayer == null || Game.Stage != NetworkLobbyStage.Playing)
            {
                return;
            }

            PlayerNotification.ShowWarningNotification(WellKnownKeys.GameNotifications.Session.PlayerLeft.Key, addToLog: false, warningDuration: 2f, args: networkPlayer.Name);
            PlayerNotification.AddCombatText(WellKnownKeys.GameNotifications.Session.PlayerLeft.Key, CombatTextSeverity.Common, args: new PlayerLogParameter(networkPlayer));
        }

        protected NetworkPlayer CleanupPlayer(long playerId)
        {
            var existingPlayer = GetPlayer(playerId);
            if (existingPlayer == null)
            {
                return null;
            }

            CleanupPlayer(existingPlayer);

            return existingPlayer;
        }

        protected void CleanupPlayer(NetworkPlayer player)
        {
            Game.Players.Remove(player);

            var defaultOwner = GetHost();
            foreach (var characterOwnership in Game.Characters)
            {
                if (characterOwnership.Owner != player)
                {
                    continue;
                }

                characterOwnership.Owner = defaultOwner;
            }
        }

        protected void ResetCharacterOwnership()
        {
            var host = GetHost();

            foreach (var character in Game.Characters)
            {
                character.Owner = host;
            }

            Game.CharactersOwnershipHistory.Clear();
        }

        protected void UpdateRespecWindowStateOnPlayerLeave(long playerId)
        {
            RemovePlayerFromTracker(Game.PlayersInRespecWindow, playerId);

            var respecUnitId = LevelingInteraction.GetCurrentRespecWindowUnitId();
            UpdateLevelingRespecUIState(respecUnitId);
        }

        protected void TransferSaveGame(bool requiresProgress)
        {
            var initialMessage = new NotifySaveGameInfoChanged
            {
                AutoStart = Game.StartUp.AutoStart,
                GameId = Game.Id,
                LoadedSaveSeed = Game.LoadedSaveSeed,
                IsNewGameSequence = Game.StartUp.IsNewGameSequence,
                PlayerId = Game.LocalPlayerId
            };

            if (Game.StartUp.IsNewGameSequence)
            {
                Send(initialMessage);
                return;
            }

            var settings = SettingsService.GetSettings();
            var content = FileSystem.GetRawFileContent(Game.StartUp.SavePath);
            if (content == null)
            {
                PlayerNotification.ShowModalMessage(WellKnownKeys.SysMessages.FailedToReadSave.Key, args: Game.StartUp.SavePath);
                Logger.LogError("Unable to read save game file. Path={Path}", Game.StartUp.SavePath);
                return;
            }

            var chunks = (content.Length + settings.SaveGameChunkSize - 1) / settings.SaveGameChunkSize;
            initialMessage.ExpectedChunks = chunks;
            initialMessage.ContentSize = content.Length;
            Send(initialMessage);

            Game.StartUp.SaveGameTransfer = new NetworkChunkedContentTransfer
            {
                Content = content,
                TotalChunks = chunks,
                // allows sending some other data in between batches,
                BatchSize = (int)(chunks * 0.05)
            };

            foreach (var player in Game.Players)
            {
                if (player.Id == Game.LocalPlayerId)
                {
                    continue;
                }

                var transferData = GetSaveGameTransferData(player.Id);
                transferData.CurrentOffset = 0;
                transferData.ConfirmedChunk = 0;
                transferData.MaxOffset = requiresProgress ? settings.SaveGameChunkSize : content.Length;

                SendSaveChunks(player.Id, content, transferData, settings.SaveGameChunkSize);
            }

            Logger.LogInformation("Save game transfer has been initiated. ContentSize={ContentSize}, TotalChunks={TotalChunks}, BatchSize={BatchSize}", Game.StartUp.SaveGameTransfer.Content.Length, Game.StartUp.SaveGameTransfer.TotalChunks, Game.StartUp.SaveGameTransfer.BatchSize);
        }

        protected NetworkChunkedContentTransferData GetSaveGameTransferData(long playerId)
        {
            return Game.StartUp.SaveGameTransfer.Data.GetOrAdd(playerId, k => new NetworkChunkedContentTransferData());
        }

        protected void SendSaveChunks(long playerId, byte[] content, NetworkChunkedContentTransferData transferData, int chunkSize)
        {
            var chunkNumber = transferData.ConfirmedChunk;
            for (; transferData.CurrentOffset < transferData.MaxOffset;)
            {
                chunkNumber++;

                int size = Math.Min(chunkSize, transferData.MaxOffset - transferData.CurrentOffset);
                var chunkMessage = new NotifySaveGameChunkCreated
                {
                    ChunkNumber = chunkNumber,
                    Content = content.AsSpan(transferData.CurrentOffset, size).ToArray(),
                };

                Send(playerId, chunkMessage);
                transferData.CurrentOffset += size;
            }

            transferData.SentChunk = chunkNumber;
        }

        protected HashSet<long> AddPlayerReadyStatus(PlayerTurnReadinessType playerReadinessType, long playerId, string unitId)
        {
            try
            {
                lock (ActionLock)
                {
                    var tracker = GetPlayerTurnReadinessTracker(playerReadinessType);
                    if (tracker == null)
                    {
                        Logger.LogError("Unable to find readiness tracker for provided type. ReadinessType={ReadinessTypeName}, PlayerId={PlayerId}, UnitId={UnitId}", playerReadinessType, playerId, unitId);
                        return null;
                    }

                    var isFirstAdd = !tracker.TryGetValue(unitId, out var readyPlayers) || !readyPlayers.Contains(playerId);

                    var players = tracker.AddOrUpdate(unitId,
                         key => new HashSet<long>(collection: [playerId]),
                         (key, existing) =>
                         {
                             existing.Add(playerId);
                             return existing;
                         });

                    if (isFirstAdd)
                    {
                        Logger.LogInformation("Player ready status has been confirmed. ReadinessType={ReadinessTypeName}, Key={Key}, PlayersCount={PlayersCount}, KeysCount={KeysCount}", playerReadinessType, unitId, tracker[unitId].Count, tracker.Keys.Count);
                    }

                    return players;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to confirm player readiness. PlayerId={PlayerId}, UnitId={UnitId}", playerId, unitId);
                throw;
            }
        }

        protected enum PlayerTurnReadinessType
        {
            Start,
            UnitSynchronization,
            UnitJoinedMidCombat
        }

        protected void LoadSavedGame()
        {
            try { PrepareCombatSaveLoad(Game.StartUp.SavePath); }
            catch (Exception error)
            {
                Logger.LogError(error, "[CombatSave] Load preflight refused; existing world retained");
                PlayerNotification.ShowModalMessage(WellKnownKeys.LobbyWindow.RealTime.SaveCombatLoadFailed.Key);
                return;
            }
            ResetRealTimeRuleRolls();
            ResetOnSaveLoad();

            BeginSaveLoad();
            _nativeSaveLoadStarted = true;
            Game.ForcedPause = null;
            GameInteraction.SetPause(false);
            // it's important to use different loading method for players who joined mid-game
            if (Game.Stage == NetworkLobbyStage.Playing)
            {
                Game.Id = GameInteraction.QuickLoadGame(Game.StartUp.SavePath);
            }
            else
            {
                var localPlayer = GetPlayer(Game.LocalPlayerId);
                ReadyChanged(localPlayer, true);

                var status = NetworkLobbySyncStatus.Succeed;
                UpdateLobbySyncStatus(localPlayer, status);
                var message = new NotifySaveGameSyncStatusChanged { PlayerId = localPlayer.Id, Status = status.ToString() };
                Send(message);

                Game.Id = GameInteraction.LoadGameFromMainMenu(Game.StartUp.SavePath);
            }

            SetLobbyStage(NetworkLobbyStage.Loading);
        }

        protected NetworkPlayer GetHost()
        {
            lock (ActionLock)
            {
                return Game.Players.First(p => p.IsHost);
            }
        }

        protected void ResetGameIdGenerator()
        {
            Logger.LogInformation("Resetting id counters. GameId={GameId}", Game.Id);
            ValueGenerator.ResetUniqueIdCounters(Game.Id);
        }

        protected void SoftReset()
        {
            Logger.LogInformation("Doing soft reset");
            lock (ActionLock)
            {
                _levelingDispatchGeneration++;
                var interrupted = IsCurrentLevelingIdentity(Game.Leveling?.Identity) ? Game.Leveling.Identity
                    : _deferredLevelings.Values.FirstOrDefault(value => ReferenceEquals(value.Session, Game)
                        && !value.TerminalApplied && IsCurrentLevelingIdentity(value.Identity))?.Identity;
                if (interrupted != null)
                    FailLevelingSynchronization(Game, interrupted, new InvalidOperationException("leveling-interrupted-by-area-reset"));
                Game.Leveling = null;
                _deferredLevelings.Clear();
                _activeDeferredLeveling = null;
                _replayingDeferredLeveling = false;
            }
            Game.StartUp = null;
            Game.Combat = null;
            Game.ArmyCombat = null;
            _pauseCooldown = null;
            ResetLastCombatTurn();
            ValueGenerator.ResetSeededGenerators(IdentifierLifetime.Area, IdentifierLifetime.Combat, IdentifierLifetime.CombatTurn);

            ResetPlayersTracker(Game.PlayersInSkipTime);
            ResetPlayersTracker(Game.PlayersInGlobalMapLocationMessage);
            ResetPlayersTracker(Game.PlayersInZoneLoot);
            ResetPlayersTracker(Game.PlayersInDialogPopup);
            ResetPlayersTracker(Game.PlayersInCharacterSelectionWindow);
            ResetPlayersTracker(Game.PlayersInDungeonGameOver);

            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmyInfo);
            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmyInfoMerge);
            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmySetLeader);
            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmyBuyLeader);
            ResetPlayersTracker(Game.PlayersInGlobalMapRecruitment);
            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmyLeaderLeveling);
        }

        protected void ResetLastCombatTurn()
        {
            Game.LastCombatTurn = null;
            Logger.LogInformation("Last combat turn has been reset");
        }

        protected void SetCombatStage(NetworkCombatStage combatStage, bool isSilent = false)
        {
            var current = Game.Combat.Stage;
            Game.Combat.Stage = combatStage;
            Logger.LogInformation("Combat stage has been changed. From={From}, To={To}", current, combatStage);
            PlayerNotification.AddCombatText(WellKnownKeys.GameNotifications.Combat.StageChanged.Key, CombatTextSeverity.Debug, current, combatStage);

            if (!isSilent)
            {
                OnCombatStageChanged(combatStage);
            }
        }

        protected void UpdateUIStateOnFeatureControlChange(NetworkPlayerControlledFeature feature)
        {
            Logger.LogInformation("Updating UI on feature control change. Feature={Feature}", feature);
            switch (feature)
            {
                case NetworkPlayerControlledFeature.CrusadeArmyCombat:
                    UpdateGlobalMapCrusadeArmyBattleResultsUIState();

                    var canControlTacticalCombat = HasControlOverFeature(NetworkPlayerControlledFeature.CrusadeArmyCombat);
                    CombatInteraction.UpdateTacticalCombatUIState(canControlTacticalCombat);
                    break;
                case NetworkPlayerControlledFeature.Dialogs:
                    var canContinue = (Game.DialogState?.IsCueWitnessedByAll ?? false) && HasControlOverFeature(NetworkPlayerControlledFeature.Dialogs);
                    DialogInteraction.SetDialogContinueButtonState(canContinue);
                    break;
                case NetworkPlayerControlledFeature.VendorDeal:
                    GameInteraction.RefreshVendorDealButtonState();
                    break;
            }
        }

        protected void UpdateFeatureControl(NetworkPlayerControlledFeature feature, long playerId)
        {
            if (Game.FeatureControllers.TryGetValue(feature, out var controlledByPlayerId) && controlledByPlayerId == playerId)
            {
                return;
            }

            Game.FeatureControllers.AddOrUpdate(feature, playerId, (key, existing) => playerId);

            if (Game.Stage == NetworkLobbyStage.Playing)
            {
                UpdateUIStateOnFeatureControlChange(feature);

                var player = GetPlayer(playerId);
                if (player != null)
                {
                    PlayerNotification.AddCombatText(WellKnownKeys.GameNotifications.Session.AdvancedControlChanged.Key, CombatTextSeverity.Common, feature, new PlayerLogParameter(player));
                }
            }
        }

        protected virtual void OnCombatStageChanged(NetworkCombatStage combatStage)
        {
        }

        protected void SetCombatTurnStage(NetworkCombatTurnStage combatTurnStage)
        {
            var current = Game.Combat.Turn.Stage;
            Game.Combat.Turn.Stage = combatTurnStage;
            Logger.LogInformation("Combat turn stage has been changed. From={From}, To={To}", current, combatTurnStage);
        }

        protected string StoreSaveGameContent(byte[] content)
        {
            if (content == null)
            {
                return null;
            }

            var persistentDataPath = GameInteraction.GetPersistentDataPath();
            var savePath = Path.Combine(persistentDataPath, "Saved Games", "latest received save.zks");
            if (!FileSystem.WriteFile(savePath, content))
            {
                PlayerNotification.ShowModalMessage(WellKnownKeys.SysMessages.FailedToStoreSave.Key, args: savePath);
                return null;
            }

            return savePath;
        }

        protected bool ShouldNotifyAboutAbility(string sourceUnitId)
        {
            if (Game.ArmyCombat?.Turn != null)
            {
                return HasControlOverFeature(NetworkPlayerControlledFeature.CrusadeArmyCombat) && !Game.ArmyCombat.Turn.IsAI;
            }

            if (Game.Combat != null && Game.Combat.Turn == null)
            {
                Logger.LogWarning("Mid-fight action. UnitId={UnitId}", sourceUnitId);
                return false;
            }

            var shouldNotify = IsControlledByLocalPlayer(sourceUnitId);
            return shouldNotify;
        }

        protected List<NetworkPlayer> GetMissingPlayers(string key, ConcurrentDictionary<string, HashSet<long>> playersReadinessTracker)
        {
            var players = GetPlayers();
            if (!playersReadinessTracker.TryGetValue(key, out var readyPlayers))
            {
                return players;
            }

            var notReady = players.Where(x => !readyPlayers.Contains(x.Id)).ToList();
            return notReady;
        }

        protected bool ConfirmReadiness(PlayerTurnReadinessType playerTurnReadinessType, long playerId, string unitId)
        {
            var tracker = GetPlayerTurnReadinessTracker(playerTurnReadinessType);
            var missingPlayers = GetMissingPlayers(unitId, tracker);
            return !missingPlayers.Any(p => p.Id == playerId);
        }

        protected NetworkCharacter GetPartyCharacter(string unitId)
        {
            if (string.IsNullOrEmpty(unitId))
            {
                return null;
            }

            var realCharacterId = GameInteraction.GetPetOwnerId(unitId) ?? unitId;

            return Game.Characters.FirstOrDefault(c => string.Equals(c.UnitId, realCharacterId, StringComparison.OrdinalIgnoreCase));
        }

        protected NetworkCharacter FindCharacter(NetworkCharacter character)
        {
            // Host/Join window doesn't load unit ids from the save, so we have to rely on indexes :(
            if (Game.Stage == NetworkLobbyStage.Lobby && character.Index.HasValue && Game.Characters.Count > character.Index.Value)
            {
                var indexedCharacter = Game.Characters[character.Index.Value];
                return indexedCharacter;
            }

            var actualCharacter = Game.Characters.FirstOrDefault(x => !string.IsNullOrEmpty(x.UnitId)
                && string.Equals(x.UnitId, character.UnitId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.Name, character.Name, StringComparison.OrdinalIgnoreCase));

            return actualCharacter;
        }

        protected void SetAreaSeed(int seed)
        {
            if (Game.CurrentArea == null)
            {
                Logger.LogError("Trying to set seed for not loaded area yet");
                return;
            }

            Game.CurrentArea.Seed = seed;
            Logger.LogInformation("Area seed has been set. Seed={Seed}", Game.CurrentArea.Seed);
        }

        protected NetworkPlayer GetPlayer(long playerId)
        {
            lock (ActionLock)
            {
                return Game?.Players.FirstOrDefault(p => p.Id == playerId);
            }
        }

        protected bool IsGameModeAllowedToRun(GameModeType type)
        {
            return type != GameModeType.EscMode && type != GameModeType.FullScreenUi;
        }

        protected void UpdateInGameCharacterOwnershipChange(NetworkCharacter networkCharacter)
        {
            if (Game.Stage != NetworkLobbyStage.Playing)
            {
                return;
            }

            GameInteraction.ReselectSelectedCharacters();
            PlayerNotification.AddCombatText(WellKnownKeys.GameNotifications.Session.CharacterOwnerChanged.Key, CombatTextSeverity.Common, new PlayerLogParameter(networkCharacter.Owner), new UnitLogParameter(networkCharacter.UnitId));

            if (Game.Combat?.Turn != null)
            {
                lock (ActionLock)
                {
                    if (Game.Combat?.Turn != null)
                    {
                        Game.Combat.Turn.IsLocalPlayer = IsControlledByLocalPlayer(Game.Combat.Turn.UnitId);
                    }
                }
            }
        }

        protected void UpdateLobbySyncStatus(long playerId, NetworkLobbySyncStatus syncStatus)
        {
            var player = GetPlayer(playerId);
            if (player == null)
            {
                Logger.LogWarning("Unable to update lobby sync status for missing player. PlayerId={PlayerId}", playerId);
                return;
            }

            UpdateLobbySyncStatus(player, syncStatus);
        }

        protected void UpdateLobbySyncStatus(NetworkPlayer player, NetworkLobbySyncStatus syncStatus)
        {
            player.LobbySyncStatus = syncStatus;
            InvokeOnPlayersChanged();
        }

        protected void UpdateReadyStatus(long playerId, bool isReady)
        {
            lock (ActionLock)
            {
                var existingPlayer = GetPlayer(playerId);
                if (existingPlayer == null)
                {
                    Logger.LogWarning("Unable to update ready status for missing player. PlayerId={PlayerId}", playerId);
                    return;
                }

                existingPlayer.IsReady = isReady;

                InvokeOnPlayersChanged();
            }
        }

        protected void InvokeOnPlayersChanged()
        {
            var players = GetPlayers();
            OnPlayersChanged?.Invoke(Game.Stage, players);
        }

        protected void UpdateCharacterOwnershipHistory(NetworkCharacter ownership)
        {
            if (Game.CharactersOwnershipHistory.TryGetValue(ownership.UnitId, out var playerId) && playerId == ownership.Owner.Id)
            {
                return;
            }

            Game.CharactersOwnershipHistory[ownership.UnitId] = ownership.Owner.Id;
            Logger.LogInformation("Character ownership history has been updated. CharacterUnitId={CharacterUnitId}, PlayerId={PlayerId}", ownership.UnitId, ownership.Owner.Id);
        }

        protected int CreateRandomSeed()
        {
            var seed = ValueGenerator.GetRandom(IdentifierLifetime.Area, Guid.NewGuid().ToString()).Next(int.MinValue, int.MaxValue);
            return seed;
        }

        protected int GetSyncedPlayersCount()
        {
            return GetSyncedPlayers().Count;
        }

        protected List<NetworkPlayer> GetSyncedPlayers()
        {
            lock (ActionLock)
            {
                return [.. Game.Players.Where(x => x.LobbySyncStatus == NetworkLobbySyncStatus.Succeed)];
            }
        }

        protected NetworkContentState GetInstalledContent()
        {
            var content = GameInteraction.GetInstalledContent();
            return content;
        }

        protected void ResetCombatTurn()
        {
            lock (ActionLock)
            {
                SaveLastCombatTurn();
                if (Game.Combat != null)
                {
                    Game.Combat.Turn = null;
                }
            }
        }

        protected void RefreshUIOnPlayerDisconnect(long playerId)
        {
            UpdateRestUIState();

            if (Game.Rest != null)
            {
                RemovePlayerFromTracker(Game.Rest.PlayersFinishedRest, playerId);
                UpdateRestResultsUIState();
            }

            RemovePlayerFromTracker(Game.PlayersInSkipTime, playerId);
            UpdateSkipTimeUIState();

            RemovePlayerFromTracker(Game.PlayersInGroupChanger, playerId);
            UpdateGroupManagerUIState();

            RemovePlayerFromTracker(Game.PlayersInZoneLoot, playerId);
            UpdateZoneLootUIState();

            UpdateRespecWindowStateOnPlayerLeave(playerId);

            UpdateCharacterSelectionUIState();

            Game.PlayersInGlobalMapMode.TryRemove(playerId, out _);
            UpdateGlobalMapUIState();

            RemovePlayerFromTracker(Game.PlayersInGlobalMapCrusadeArmyBattleResults, playerId);
            UpdateGlobalMapCrusadeArmyBattleResultsUIState();

            RemovePlayerFromTracker(Game.PlayersInGlobalMapCommonPopup, playerId);
            UpdateGlobalMapCommonPopupUIState(null);

            RemovePlayerFromTracker(Game.PlayersInGlobalMapCombatResults, playerId);
            UpdateGlobalMapCombatResultsUIState();
        }

        protected async Task<bool> WaitWhileTrue(Func<bool> condition, string warningMessage, TimeSpan? awaiterTimeout = null)
        {
            var effectiveTimeout = awaiterTimeout ?? TimeSpan.FromSeconds(20);
            if (condition())
            {
                Logger.LogInformation("{Message}. Timeout={Timeout}, CombatStage={CombatStage}, TurnUnitId={TurnUnitId}", warningMessage, effectiveTimeout, Game?.Combat?.Stage, Game?.Combat?.Turn?.UnitId);
                using var timeoutTokenSource = new CancellationTokenSource(effectiveTimeout);
                while (condition())
                {
                    await Task.Delay(10);

                    if (timeoutTokenSource.IsCancellationRequested)
                    {
                        Logger.LogError("Combat wait timed out. Reason={Reason}, Timeout={Timeout}, CombatStage={CombatStage}, TurnUnitId={TurnUnitId}, TurnStage={TurnStage}, Round={Round}", warningMessage, effectiveTimeout, Game?.Combat?.Stage, Game?.Combat?.Turn?.UnitId, Game?.Combat?.Turn?.Stage, Game?.Combat?.Round);
                        return false;
                    }
                }

                Logger.LogInformation("Combat wait completed. Reason={Reason}, CombatStage={CombatStage}, TurnUnitId={TurnUnitId}", warningMessage, Game?.Combat?.Stage, Game?.Combat?.Turn?.UnitId);
            }

            return true;
        }

        protected bool IsOnSameDialogState(NetworkDialogState local, NetworkDialog remoteDialog, string remoteCue)
        {
            return local?.Dialog != null
                && remoteDialog != null
                && string.Equals(local.Dialog.Name, remoteDialog.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(local.CurrentCueName, remoteCue, StringComparison.OrdinalIgnoreCase);
        }

        protected void ResetGlobalMapCounters()
        {
            Game.PlayersInGlobalMapMode.Clear();
            Logger.LogInformation("Global map counters have been reset");
        }

        protected void StartNewGameSequence()
        {
            Logger.LogInformation("Starting new game sequence");
            SetLobbyStage(NetworkLobbyStage.NewGameSequence);
            OnNewGameSequenceStarted?.Invoke(true);

            var mainCharacterId = Game.Characters.First().UnitId;
            GameInteraction.StartNewGameSequence(
                mainCharacterId,
                onBack: () =>
                {
                    Logger.LogInformation("New game sequence has been cancelled");
                    SetLobbyStage(NetworkLobbyStage.Lobby);
                    foreach (var player in Game.Players)
                    {
                        player.IsReady = false;
                        UpdateLobbySyncStatus(player, NetworkLobbySyncStatus.Pending);
                    }

                    if (CanMakeNewGameSequenceDecisions())
                    {
                        var message = new NotifyNewGameSequenceTerminated();
                        Send(message);
                    }

                    ResetPlayersTracker(Game.StartUp.ReadyPlayers);
                    OnNewGameSequenceStarted?.Invoke(false);
                },
                onStart: () =>
                {
                    ResetPlayersTracker(Game.StartUp.ReadyPlayers);

                    OnForceLevelingUI(mainCharacterId, NetworkLevelingType.NewGameSequence);
                    Logger.LogInformation("New game sequence has been finished");

                    if (CanMakeNewGameSequenceDecisions())
                    {
                        var message = new NotifyNewGameSequenceLevelingStarted();
                        Send(message);
                    }
                },
                onCharacterCreated: (character) =>
                {
                    var fakeCharacter = Game.Characters.First();
                    fakeCharacter.Name = character.Name;
                    fakeCharacter.Portrait = character.Portrait;
                });
        }

        protected virtual void OnSaveGameChunkSaved(int chunkNumber)
        {
        }

        protected virtual void OnSaveGameChunksTransferCompleted()
        {

        }
        protected virtual void SetupNetworkMessageHandlers()
        {
            _networkConnection
                // lobby
                .On<NotifySaveGameChunkCreated>(OnNotifySaveGameChunkCreated)
                .On<NotifySaveGameInfoChanged>(OnNotifySaveGameInfoChanged)
                .On<NotifyPlayerReadyStatusChanged>(OnNotifyPlayerReadyStatusChanged)

                // leveling
                .On<NotifyLevelingClassArchetypeSelected>(DeferLevelingMessages<NotifyLevelingClassArchetypeSelected>(OnNotifyLevelingClassArchetypeSelected))
                .On<NotifyLevelingMythicClassSelected>(DeferLevelingMessages<NotifyLevelingMythicClassSelected>(OnNotifyLevelingMythicClassSelected))
                .On<NotifyLevelingClassSelected>(DeferLevelingMessages<NotifyLevelingClassSelected>(OnNotifyLevelingClassSelected))
                .On<NotifyLevelingPhaseWitnessed>(DeferLevelingMessages<NotifyLevelingPhaseWitnessed>(OnNotifyLevelingPhaseWitnessed))
                .On<NotifyLevelingPhaseChanged>(DeferLevelingMessages<NotifyLevelingPhaseChanged>(OnNotifyLevelingPhaseChanged))
                .On<NotifyLevelingSkillPointIncreased>(DeferLevelingMessages<NotifyLevelingSkillPointIncreased>(OnNotifyLevelingSkillPointIncreased))
                .On<NotifyLevelingSkillPointDecreased>(DeferLevelingMessages<NotifyLevelingSkillPointDecreased>(OnNotifyLevelingSkillPointDecreased))
                .On<NotifyLevelingAbilityScoreIncreased>(DeferLevelingMessages<NotifyLevelingAbilityScoreIncreased>(OnNotifyLevelingAbilityScoreIncreased))
                .On<NotifyLevelingAbilityScoreDecreased>(DeferLevelingMessages<NotifyLevelingAbilityScoreDecreased>(OnNotifyLevelingAbilityScoreDecreased))
                .On<NotifyLevelingPortraitSelected>(DeferLevelingMessages<NotifyLevelingPortraitSelected>(OnNotifyLevelingPortraitSelected))
                .On<NotifyLevelingVoiceSelected>(DeferLevelingMessages<NotifyLevelingVoiceSelected>(OnNotifyLevelingVoiceSelected))
                .On<NotifyLevelingRaceSelected>(DeferLevelingMessages<NotifyLevelingRaceSelected>(OnNotifyLevelingRaceSelected))
                .On<NotifyLevelingGenderSelected>(DeferLevelingMessages<NotifyLevelingGenderSelected>(OnNotifyLevelingGenderSelected))
                .On<NotifyLevelingAlignmentSelected>(DeferLevelingMessages<NotifyLevelingAlignmentSelected>(OnNotifyLevelingAlignmentSelected))
                .On<NotifyLevelingNameChanged>(DeferLevelingMessages<NotifyLevelingNameChanged>(OnNotifyLevelingNameChanged))
                .On<NotifyLevelingBodyTypeAppearanceChanged>(DeferLevelingMessages<NotifyLevelingBodyTypeAppearanceChanged>(OnNotifyLevelingBodyTypeAppearanceChanged))
                .On<NotifyLevelingBodyColorAppearanceChanged>(DeferLevelingMessages<NotifyLevelingBodyColorAppearanceChanged>(OnNotifyLevelingBodyColorAppearanceChanged))
                .On<NotifyLevelingEyesColorAppearanceChanged>(DeferLevelingMessages<NotifyLevelingEyesColorAppearanceChanged>(OnNotifyLevelingEyesColorAppearanceChanged))
                .On<NotifyLevelingFaceAppearanceChanged>(DeferLevelingMessages<NotifyLevelingFaceAppearanceChanged>(OnNotifyLevelingFaceAppearanceChanged))
                .On<NotifyLevelingHairColorAppearanceChanged>(DeferLevelingMessages<NotifyLevelingHairColorAppearanceChanged>(OnNotifyLevelingHairColorAppearanceChanged))
                .On<NotifyLevelingHairStyleAppearanceChanged>(DeferLevelingMessages<NotifyLevelingHairStyleAppearanceChanged>(OnNotifyLevelingHairStyleAppearanceChanged))
                .On<NotifyLevelingHornsAppearanceChanged>(DeferLevelingMessages<NotifyLevelingHornsAppearanceChanged>(OnNotifyLevelingHornsAppearanceChanged))
                .On<NotifyLevelingHornsColorAppearanceChanged>(DeferLevelingMessages<NotifyLevelingHornsColorAppearanceChanged>(OnNotifyLevelingHornsColorAppearanceChanged))
                .On<NotifyLevelingPrimaryOutfitColorAppearanceChanged>(DeferLevelingMessages<NotifyLevelingPrimaryOutfitColorAppearanceChanged>(OnNotifyLevelingPrimaryOutfitColorAppearanceChanged))
                .On<NotifyLevelingSecondaryOutfitColorAppearanceChanged>(DeferLevelingMessages<NotifyLevelingSecondaryOutfitColorAppearanceChanged>(OnNotifyLevelingSecondaryOutfitColorAppearanceChanged))
                .On<NotifyLevelingScarAppearanceChanged>(DeferLevelingMessages<NotifyLevelingScarAppearanceChanged>(OnNotifyLevelingScarAppearanceChanged))
                .On<NotifyLevelingTattooAppearanceChanged>(DeferLevelingMessages<NotifyLevelingTattooAppearanceChanged>(OnNotifyLevelingTattooAppearanceChanged))
                .On<NotifyLevelingTattooColorAppearanceChanged>(DeferLevelingMessages<NotifyLevelingTattooColorAppearanceChanged>(OnNotifyLevelingTattooColorAppearanceChanged))
                .On<NotifyLevelingWarpaintAppearanceChanged>(DeferLevelingMessages<NotifyLevelingWarpaintAppearanceChanged>(OnNotifyLevelingWarpaintAppearanceChanged))
                .On<NotifyLevelingWarpaintColorAppearanceChanged>(DeferLevelingMessages<NotifyLevelingWarpaintColorAppearanceChanged>(OnNotifyLevelingWarpaintColorAppearanceChanged))
                .On<NotifyLevelingRacialAbilityScoreBonusChanged>(DeferLevelingMessages<NotifyLevelingRacialAbilityScoreBonusChanged>(OnNotifyLevelingRacialAbilityScoreBonusChanged))
                .On<NotifyLevelingBirthMonthChanged>(DeferLevelingMessages<NotifyLevelingBirthMonthChanged>(OnNotifyLevelingBirthMonthChanged))
                .On<NotifyLevelingBirthDayChanged>(DeferLevelingMessages<NotifyLevelingBirthDayChanged>(OnNotifyLevelingBirthDayChanged))
                .On<NotifyLevelingFeatureSelected>(DeferLevelingMessages<NotifyLevelingFeatureSelected>(OnNotifyLevelingFeatureSelected))
                .On<NotifyLevelingSpellChosen>(DeferLevelingMessages<NotifyLevelingSpellChosen>(OnNotifyLevelingSpellChosen))
                .On<NotifyLevelingSpellRemoved>(DeferLevelingMessages<NotifyLevelingSpellRemoved>(OnNotifyLevelingSpellRemoved))
                .On<NotifyLevelingCompleted>(DeferLevelingMessages<NotifyLevelingCompleted>(OnNotifyLevelingCompleted))
                .On<NotifyLevelingTerminated>(DeferLevelingMessages<NotifyLevelingTerminated>(OnNotifyLevelingTerminated))
                // respec selector & window
                .On<NotifyLevelingRespecCompleted>(OnNotifyLevelingRespecCompleted)
                .On<NotifyLevelingRespecWindowShown>(OnNotifyLevelingRespecWindowShown)
                .On<NotifyLevelingRespecLevelUp>(OnNotifyLevelingRespecLevelUp)
                .On<NotifyLevelingRespecMythicLevelUp>(OnNotifyLevelingRespecMythicLevelUp)

                // new game sequence
                .On<NotifyNewGameSequenceWitnessed>(OnNotifyNewGameSequenceWitnessed)

                // character selection window
                .On<NotifyCharacterSelectionWindowShown>(OnNotifyCharacterSelectionWindowShown)

                // spellbook management
                .On<NotifySpellMemorized>(OnNotifySpellMemorized)
                .On<NotifySpellForgotten>(OnNotifySpellForgotten)
                .On<NotifySpellSlotsSwapped>(OnNotifySpellSlotsSwapped)
                .On<NotifyMetamagicSpellCreated>(OnNotifyMetamagicSpellCreated)
                .On<NotifyMagicHackSpellCreated>(OnNotifyMagicHackSpellCreated)
                .On<NotifyCustomSpellRemoved>(OnNotifyCustomSpellRemoved)

                // vendor interaction
                .On<NotifyVendorItemTransferred>(OnNotifyVendorItemTransferred)
                .On<NotifyVendorDealMade>(OnNotifyVendorDealMade)
                .On<NotifyVendorWindowClosed>(OnNotifyVendorWindowClosed)

                // rest
                .On<NotifyRestBanterInterrupted>(OnNotifyRestBanterInterrupted)
                .On<NotifyRestEnded>(OnNotifyRestEnded)
                .On<NotifyCapitalModeRestInitiated>(OnNotifyCapitalModeRestInitiated)

                // combat
                .On<NotifyUnitJoinedMidCombat>(OnNotifyUnitJoinedMidCombat)
                .On<NotifyUnitAttacked>(OnNotifyUnitAttacked)
                .On<NotifyCombatTurnDelayed>(OnNotifyCombatTurnDelayed)
                .On<NotifyUnitKilled>(OnNotifyUnitKilled)

                // global map & crusade combat
                .On<NotifyGlobalMapTravelerModeChanged>(OnNotifyGlobalMapTravelerModeChanged)
                .On<NotifyGlobalMapLocationMessageShown>(OnNotifyGlobalMapLocationMessageShown)
                .On<NotifyGlobalMapEncounterMessageShown>(OnNotifyGlobalMapEncounterMessageShown)
                .On<NotifyGlobalMapCombatResultsShown>(OnNotifyGlobalMapCombatResultsShown)
                .On<NotifyTacticalCombatTurnInitialized>(OnNotifyTacticalCombatTurnInitialized)
                .On<NotifyCrusadeArmyBattleResultsShown>(OnNotifyCrusadeArmyBattleResultsShown)
                .On<NotifyGlobalMapCrusadeArmyMergeCartShown>(OnNotifyGlobalMapCrusadeArmyInfoMergeShown)
                .On<NotifyGlobalMapCrusadeArmySetLeaderShown>(OnNotifyGlobalMapCrusadeArmySetLeaderShown)
                .On<NotifyGlobalMapCrusadeArmyBuyLeaderShown>(OnNotifyGlobalMapCrusadeArmyBuyLeaderShown)
                .On<NotifyGlobalMapCrusadeArmyLeaderLevelingShown>(OnNotifyGlobalMapCrusadeArmyLeaderLevelingShown)
                .On<NotifyGlobalMapCrusadeArmyLeaderLevelingStarted>(OnNotifyGlobalMapCrusadeArmyLeaderLevelingStarted)
                .On<NotifyKingdomEntered>(OnNotifyKingdomEntered)
                .On<NotifyKingdomExited>(OnNotifyKingdomExited)
                .On<NotifyKingdomLoaded>(OnNotifyKingdomLoaded)
                .On<NotifyKingdomUnloaded>(OnNotifyKingdomUnloaded)

                // crusade combat
                .On<NotifyTacticalCombatRetreated>(OnNotifyTacticalCombatRetreated)
                .On<NotifyTacticalCombatAccelerationChanged>(OnNotifyTacticalCombatAccelerationChanged)
                .On<NotifyTacticalCombatTurnPostponed>(OnNotifyTacticalCombatTurnPostponed)
                .On<NotifyTacticalCombatTotalDefenseUsed>(OnNotifyTacticalCombatTotalDefenseUsed)
                .On<NotifyTacticalUnitAttackCommandExecuted>(OnNotifyTacticalUnitAttackCommandExecuted)
                .On<NotifyTacticalUnitMoveToCommandExecuted>(OnNotifyTacticalUnitMoveToCommandExecuted)
                .On<NotifyCrusadeArmyBattleResultsClosed>(OnNotifyCrusadeArmyBattleResultsClosed)

                // kingdom
                .On<NotifyKingdomSettlementLoaded>(OnNotifyKingdomSettlementLoaded)

                // map objects
                .On<NotifyOvertipInteracted>(OnNotifyOvertipInteracted)
                .On<NotifyTrapDisarmRolled>(OnNotifyTrapDisarmRolled)
                .On<NotifyTransitionMapShown>(OnNotifyTransitionMapShown)
                .On<NotifyLootClosed>(OnNotifyLootClosed)

                // items&inventory
                .On<NotifyLootableEntitySkinned>(OnNotifyContainerSkinned)
                .On<NotifyDropItem>(OnNotifyDropItem)
                .On<NotifyEquipmentSlotChanged>(OnNotifyEquipmentSlotChanged)
                .On<NotifyActiveHandEquipmentSetChanged>(OnNotifyActiveHandEquipmentSetChanged)
                .On<NotifyZoneLootShown>(OnNotifyZoneLootShown)
                .On<NotifyZoneLootClosed>(OnNotifyZoneLootClosed)
                .On<NotifyInventoryItemTransferred>(OnNotifyInventoryItemTransferred)
                .On<NotifyInventoryItemUsed>(OnNotifyInventoryItemUsed)
                .On<NotifyInventoryItemCopied>(OnNotifyInventoryItemCopied)
                .On<NotifyItemDescriptionRead>(OnNotifyItemDescriptionRead)

                // lockpick
                .On<NotifyMapObjectLockpicked>(OnNotifyMapObjectLockpicked)

                // abilities
                .On<NotifyUnitAutoUseAbilityChanged>(OnNotifyUnitAutoUseAbilityChanged)
                .On<NotifyAbilityUsed>(OnNotifyAbilityUsed)
                .On<NotifyToggleActivatableAbility>(OnNotifyToggleActivatableAbility)

                // clicks
                .On<NotifyMapObjectClicked>(OnNotifyMapObjectClicked)

                // pausing
                .On<NotifyGamePauseStarted>(OnNotifyGamePauseStarted)

                // action bar
                .On<NotifyActionBarSlotCleared>(OnNotifyActionBarSlotCleared)
                .On<NotifyActionBarSlotMoved>(OnNotifyActionBarSlotMoved)

                // stealth
                .On<NotifyUnitStealthChanged>(OnNotifyUnitStealthChanged)

                // skip time
                .On<NotifySkipTimeOpened>(OnNotifySkipTimeOpened)

                // group management
                .On<NotifyGroupChangerOpened>(OnNotifyGroupChangerOpened)

                // dialogs
                .On<NotifyDialogPopupShown>(OnNotifyDialogPopupShown)
                .On<NotifyDialogPopupClosed>(OnNotifyDialogPopupClosed)
                .On<NotifyDialogPopupAccepted>(OnNotifyDialogPopupAccepted)

                // game modes
                .On<NotifyGameModeTypeStarted>(OnNotifyGameModeTypeStarted)
                .On<NotifyGameModeTypeEnded>(OnNotifyGameModeTypeEnded)

                // ping
                .On<NotifyPingedByPlayer>(OnNotifyPingedByPlayer)

                // cutscenes
                .On<NotifyCutsceneSkipped>(OnNotifyCutsceneSkipped)

                // unit actions
                .On<NotifyUnitMovedTo>(OnNotifyUnitMovedTo)
                .On<NotifyTurnMovementState>(OnTurnMovementState)
                .On<NotifyUnitInteractedWithUnit>(OnNotifyUnitInteractedWithUnit)
                .On<NotifyUnitLootedUnit>(OnNotifyUnitLootedUnit)

                // map objects
                .On<NotifyTrapActivated>(OnNotifyTrapActivated)
                .On<NotifyMapObjectCombinePartInteracted>(OnNotifyMapObjectCombinePartInteracted)

                // dungeon
                .On<NotifyDungeonGameOverShown>(OnNotifyDungeonGameOverShown)
                .On<NotifyDungeonBoonSelectorShown>(OnNotifyDungeonBoonSelectorShown)
                ;
        }

        private void OnNotifyCrusadeArmyBattleResultsClosed(long receivedFrom, NotifyCrusadeArmyBattleResultsClosed message)
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapCrusadeArmyBattleResults);
            GlobalMapInteraction.CloseCrusadeArmyBattleResults();
        }

        private void OnNotifyTacticalUnitMoveToCommandExecuted(long receivedFrom, NotifyTacticalUnitMoveToCommandExecuted message)
        {
            var command = Mapper.Map<NetworkTacticalUnitMoveToCommand>(message.Command);

            CombatInteraction.RunTacticalUnitMoveToCommand(command);
        }

         private void OnNotifyTacticalUnitAttackCommandExecuted(long receivedFrom, NotifyTacticalUnitAttackCommandExecuted message)
            => SafeFireAndForget(() => OnNotifyTacticalUnitAttackCommandExecutedAsync(receivedFrom, message));

         private async Task OnNotifyTacticalUnitAttackCommandExecutedAsync(long receivedFrom, NotifyTacticalUnitAttackCommandExecuted message)
        {
            var command = Mapper.Map<NetworkTacticalUnitAttackCommand>(message.Command);

            await WaitWhileTrue(() => Game.ArmyCombat == null || !string.Equals(Game.ArmyCombat.Turn.UnitId, message.Command.UnitId, StringComparison.OrdinalIgnoreCase),
                "Waiting for unit turn to start");

            CombatInteraction.RunTacticalUnitAttackCommand(command);
        }

        private void OnNotifyTacticalCombatTotalDefenseUsed(long receivedFrom, NotifyTacticalCombatTotalDefenseUsed message)
        {
            CombatInteraction.UseTacticalCombatTotalDefense();
        }

        private void OnNotifyTacticalCombatTurnPostponed(long receivedFrom, NotifyTacticalCombatTurnPostponed message)
        {
            CombatInteraction.PostponeTacticalCombatTurn();
        }

        private void OnNotifyTacticalCombatAccelerationChanged(long receivedFrom, NotifyTacticalCombatAccelerationChanged message)
        {
            CombatInteraction.SetTacticalCombatAcceleration(message.IsAccelerated);
        }

        private void OnNotifyTacticalCombatRetreated(long receivedFrom, NotifyTacticalCombatRetreated message)
        {
            CombatInteraction.RetreatFromTacticalCombat();
        }

        private void OnNotifyDungeonBoonSelectorShown(long receivedFrom, NotifyDungeonBoonSelectorShown message)
        {
            AddPlayerToTracker(Game.PlayersInDungeonBoonSelector, message.PlayerId);

            UpdateDungeonBoonSelectorUIState();
        }

        private void OnNotifyDungeonGameOverShown(long receivedFrom, NotifyDungeonGameOverShown message)
        {
            AddPlayerToTracker(Game.PlayersInDungeonGameOver, message.PlayerId);

            UpdateDungeonGameOverUIState();
        }

        private void OnNotifyLootClosed(long receivedFrom, NotifyLootClosed message)
        {
            var mapObject = Mapper.Map<NetworkMapObject>(message.MapObject);
            GameInteraction.TriggerLootClosedActions(mapObject);
        }

         private void OnNotifyGlobalMapTravelerModeChanged(long receivedFrom, NotifyGlobalMapTravelerModeChanged message)
            => SafeFireAndForget(() => OnNotifyGlobalMapTravelerModeChangedAsync(receivedFrom, message));

         private async Task OnNotifyGlobalMapTravelerModeChangedAsync(long receivedFrom, NotifyGlobalMapTravelerModeChanged message)
        {
            await WaitWhileTrue(() => Game.CurrentArea == null, "Waiting for area to load before updating traveler mode");

            var travelerMode = Mapper.Map<NetworkGlobalMapTravelerMode>(message.TravelerMode);
            RegisterGlobalMapMode(message.PlayerId, travelerMode);
            UpdateGlobalMapUIState();

            if (message.MustBeEnforced)
            {
                GlobalMapInteraction.ChangeArmyMode(travelerMode);
            }
        }

        private void OnNotifySaveGameInfoChanged(long receivedFrom, NotifySaveGameInfoChanged message)
        {
            Game.StartUp = new NetworkGameStartUp
            {
                AutoStart = message.AutoStart,
                IsNewGameSequence = message.IsNewGameSequence,
                SaveGameTransfer = new NetworkChunkedContentTransfer
                {
                    TotalChunks = message.ExpectedChunks,
                    Content = new byte[message.ContentSize]
                }
            };

            Game.Id = message.GameId;
            Game.LoadedSaveSeed = message.LoadedSaveSeed;
            if (!message.IsNewGameSequence) BeginSaveLoad();

            if (Game.StartUp.IsNewGameSequence)
            {
                OnSaveGameChunksTransferCompleted();
            }

            if (Game.Stage == NetworkLobbyStage.Playing)
            {
                var player = GetPlayer(message.PlayerId);
                PlayerNotification.ShowWarningNotification(WellKnownKeys.GameNotifications.Session.LoadingSave.Key, args: player?.Name);
            }
        }

        private void OnNotifySaveGameChunkCreated(long receivedFrom, NotifySaveGameChunkCreated message)
        {
            if (message.Content.Length > SaveSizeLimit
                || (long)Game.StartUp.SaveGameTransfer.Content.Length + message.Content.Length > SaveSizeLimit)
            {
                _networkConnection.Reset();
                Game.StartUp = null;
                PlayerNotification.ShowModalMessage(WellKnownKeys.SysMessages.AbnormalSaveGameSize.Key);
                return;
            }

            var transfer = GetSaveGameTransferData(Game.LocalPlayerId);
            message.Content.AsSpan().CopyTo(Game.StartUp.SaveGameTransfer.Content.AsSpan(transfer.CurrentOffset));
            transfer.CurrentOffset += message.Content.Length;
            transfer.ConfirmedChunk = message.ChunkNumber;

            OnSaveGameChunkSaved(message.ChunkNumber);

            if (Game.StartUp.SaveGameTransfer.TotalChunks != message.ChunkNumber)
            {
                return;
            }

            var saveGameTransfer = Game.StartUp.SaveGameTransfer;
            Logger.LogInformation("Latest save game chunk has been received. TotalChunks={TotalChunks}, ContentSize={ContentSize}", saveGameTransfer.TotalChunks, saveGameTransfer.Content?.Length);
            Game.StartUp.SavePath = StoreSaveGameContent(saveGameTransfer.Content);

            if (Game.StartUp.AutoStart)
            {
                LoadSavedGame();
                return;
            }

            OnSaveGameChunksTransferCompleted();
        }

        private void OnNotifySpellSlotsSwapped(long receivedFrom, NotifySpellSlotsSwapped message)
        {
            var slotA = Mapper.Map<NetworkSpellSlot>(message.SlotA);
            var slotB = Mapper.Map<NetworkSpellSlot>(message.SlotB);
            GameInteraction.SwapSpellSlots(message.UnitId, message.SpellbookId, message.SpellLevel, slotA, slotB);
        }

        private void OnNotifyMapObjectCombinePartInteracted(long receivedFrom, NotifyMapObjectCombinePartInteracted message)
        {
            var mapObject = Mapper.Map<NetworkMapObject>(message.MapObject);
            GameInteraction.InteractWithMapObjectCombinePart(mapObject, message.UnitId, message.PartIndex);
        }

        private void OnNotifyUnitLootedUnit(long receivedFrom, NotifyUnitLootedUnit message)
        {
            var lootUnit = Mapper.Map<NetworkUnitLootUnit>(message.LootUnit);
            CombatInteraction.LootUnit(lootUnit);
        }

        private void OnNotifyUnitInteractedWithUnit(long receivedFrom, NotifyUnitInteractedWithUnit message)
        {
            var interaction = Mapper.Map<NetworkUnitInteractWithUnit>(message.Interaction);
            CombatInteraction.InteractWithUnit(interaction);
        }

        private void OnNotifyTrapActivated(long receivedFrom, NotifyTrapActivated message)
        {
            var trapObject = Mapper.Map<NetworkMapObject>(message.Trap);
            GameInteraction.ActivateTrap(message.UnitId, trapObject);
        }

        private void OnNotifyCustomSpellRemoved(long receivedFrom, NotifyCustomSpellRemoved message)
        {
            var ability = Mapper.Map<NetworkAbility>(message.Ability);
            GameInteraction.RemoveCustomSpell(message.UnitId, ability);
        }


        private void OnNotifyMagicHackSpellCreated(long receivedFrom, NotifyMagicHackSpellCreated message)
        {
            var magicHackSpell = Mapper.Map<NetworkMagicHackSpell>(message.MagicHackSpell);
            GameInteraction.CreateMagicHackSpell(magicHackSpell);
        }

        private void OnNotifyMetamagicSpellCreated(long receivedFrom, NotifyMetamagicSpellCreated message)
        {
            var metamagicSpell = Mapper.Map<NetworkMetamagicSpell>(message.MetamagicSpell);
            GameInteraction.CreateMetamagicSpell(metamagicSpell);
        }

        private void OnNotifyTransitionMapShown(long receivedFrom, NotifyTransitionMapShown message)
        {
            AddPlayerToTracker(Game.PlayersInTransitionMap, message.PlayerId);
            UpdateTransitionMapUIState();
        }

        private void OnNotifyKingdomSettlementLoaded(long receivedFrom, NotifyKingdomSettlementLoaded message)
        {
            AddPlayerToTracker(Game.PlayersInSettlement, message.PlayerId);
            UpdateSettlementUIState();
        }

        private void OnNotifyKingdomUnloaded(long receivedFrom, NotifyKingdomUnloaded message)
        {
            RemovePlayerFromTracker(Game.PlayersInGlobalMapKingdom, message.PlayerId);
            UpdateGlobalMapUIState();
        }

        private void OnNotifyKingdomLoaded(long receivedFrom, NotifyKingdomLoaded message)
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapKingdom, message.PlayerId);
            UpdateGlobalMapKingdomUIState();
        }

        private void OnNotifyKingdomExited(long receivedFrom, NotifyKingdomExited message)
        {
            ResetPlayersTracker(Game.PlayersInGlobalMapKingdom);
            ResetGlobalMapCounters();

            GlobalMapInteraction.ExitKingdom();
        }

        private void OnNotifyKingdomEntered(long receivedFrom, NotifyKingdomEntered message)
        {
            var entryPoint = Mapper.Map<NetworkKingdomEntryPoint>(message.EntryPoint);
            ResetGlobalMapCounters();

            GlobalMapInteraction.EnterKingdom(entryPoint);
        }

        private void OnNotifyUnitMovedTo(long receivedFrom, NotifyUnitMovedTo message)
        {
            var unitMoveTo = Mapper.Map<NetworkUnitMoveTo>(message.Movement);
            ObserveTurnMovement(receivedFrom, unitMoveTo);

            CombatInteraction.MoveUnit(unitMoveTo);
        }

        private void OnNotifyItemDescriptionRead(long receivedFrom, NotifyItemDescriptionRead message)
        {
            var item = Mapper.Map<NetworkItem>(message.Item);
            GameInteraction.ReadItem(item);
        }

        private void OnNotifyInventoryItemCopied(long receivedFrom, NotifyInventoryItemCopied message)
        {
            var itemCopy = Mapper.Map<NetworkItemCopy>(message.Copy);
            GameInteraction.CopyInventoryItem(itemCopy);
        }

        private void OnNotifyUnitAutoUseAbilityChanged(long receivedFrom, NotifyUnitAutoUseAbilityChanged message)
        {
            var autoUseAbility = Mapper.Map<NetworkAutoUseAbility>(message.AutoUse);
            GameInteraction.SetUnitAutoUseAbility(autoUseAbility);
        }

        private void OnNotifyGamePauseStarted(long receivedFrom, NotifyGamePauseStarted message)
        {
            if (CombatInteraction.IsInCombat())
            {
                return;
            }

            var pause = Mapper.Map<NetworkForcedPause>(message.Pause);
            lock (ActionLock)
            {
                EnsureForcePaused(pause.Reason, pause.RemovalDelay);
                Game.ForcedPause.ReadyPlayers.Add(Game.LocalPlayerId);
                Game.ForcedPause.ReadyPlayers.Add(message.PlayerId);
            }

            GameInteraction.SetPause(true);
        }

         private void OnNotifyTrapDisarmRolled(long receivedFrom, NotifyTrapDisarmRolled message)
            => SafeFireAndForget(() => OnNotifyTrapDisarmRolledAsync(receivedFrom, message));

         private async Task OnNotifyTrapDisarmRolledAsync(long receivedFrom, NotifyTrapDisarmRolled message)
        {
            await WaitWhileTrue(() => GameInteraction.IsUnitBusy(message.TrapDisarm.UnitId), "Waiting for unit to finish actions before applying trap disarm roll");

            var trapDisarm = Mapper.Map<NetworkTrapDisarm>(message.TrapDisarm);
            GameInteraction.ApplyTrapDisarm(trapDisarm);
        }

         private void OnNotifyUnitKilled(long receivedFrom, NotifyUnitKilled message)
            => SafeFireAndForget(() => OnNotifyUnitKilledAsync(receivedFrom, message));

         private async Task OnNotifyUnitKilledAsync(long receivedFrom, NotifyUnitKilled message)
        {
            try
            {
                if (Game.Combat == null && Game.ArmyCombat == null)
                {
                    // units killed outside of combat are not synced for now
                    return;
                }

                lock (ActionLock)
                {
                    var turn = (NetworkCombatTurnBase)Game.Combat?.Turn ?? Game.ArmyCombat?.Turn;
                    if (turn != null)
                    {
                        turn.LockCounter++;
                    }
                }

                var remotelyKilledUnits = Game.Combat?.RemotelyKilledUnits ?? Game.ArmyCombat?.RemotelyKilledUnits;
                remotelyKilledUnits?.Add(message.UnitId);

                Func<bool> awaiterCondition = Game.Combat != null ? CombatInteraction.IsRiderActive : CombatInteraction.IsActiveArmyCombatUnitBusy;
                await WaitWhileTrue(awaiterCondition, "Waiting for an active unit to finish acting before checking if unit should be killed");
                var settings = SettingsService.GetSettings();
                if (settings.RemoteUnitDeathProcessingDelay > TimeSpan.Zero)
                {
                    await Task.Delay(settings.RemoteUnitDeathProcessingDelay);
                }

                var player = GetPlayer(message.PlayerId);
                var isKilled = CombatInteraction.KillUnit(player, message.UnitId);
                if (!isKilled)
                {
                    return;
                }

                if (Game.Combat?.Turn != null && string.Equals(Game.Combat.Turn.UnitId, message.UnitId, StringComparison.OrdinalIgnoreCase))
                {
                    OnTurnEnded(message.UnitId);
                    Game.Combat.Turn = null;
                    Logger.LogWarning("Turn has been reset due to auto-killed rider unit. UnitId={UnitId}", message.UnitId);
                }
            }
            finally
            {
                lock (ActionLock)
                {
                    var turn = (NetworkCombatTurnBase)Game.Combat?.Turn ?? Game.ArmyCombat?.Turn;
                    if (turn != null)
                    {
                        turn.LockCounter--;
                    }
                }
            }
        }

        private void OnNotifyCapitalModeRestInitiated(long receivedFrom, NotifyCapitalModeRestInitiated message)
        {
            GameInteraction.InitiateRest();
        }

        private void OnNotifyGlobalMapCrusadeArmyLeaderLevelingStarted(long receivedFrom, NotifyGlobalMapCrusadeArmyLeaderLevelingStarted message)
        {
            var army = Mapper.Map<NetworkGlobalMapArmy>(message.Army);
            GlobalMapInteraction.StartCrusadeArmyLeaderLeveling(army);
        }

        private void OnNotifyGlobalMapCrusadeArmyLeaderLevelingShown(long receivedFrom, NotifyGlobalMapCrusadeArmyLeaderLevelingShown message)
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapCrusadeArmyLeaderLeveling, message.PlayerId);

            UpdateGlobalMapCrusadeArmyLeaderLevelingUIState();
        }

        private void OnNotifyGlobalMapCrusadeArmyBuyLeaderShown(long receivedFrom, NotifyGlobalMapCrusadeArmyBuyLeaderShown message)
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapCrusadeArmyBuyLeader, message.PlayerId);

            UpdateGlobalMapCrusadeArmyBuyLeaderUIState();
        }

        private void OnNotifyGlobalMapCrusadeArmySetLeaderShown(long receivedFrom, NotifyGlobalMapCrusadeArmySetLeaderShown message)
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapCrusadeArmySetLeader, message.PlayerId);
            UpdateGlobalMapCrusadeArmyInfoUIStateOnSetLeader();
        }

        private void OnNotifyGlobalMapCrusadeArmyInfoMergeShown(long receivedFrom, NotifyGlobalMapCrusadeArmyMergeCartShown message)
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapCrusadeArmyInfoMerge, message.PlayerId);
            UpdateGlobalMapCrusadeArmyInfoUIStateOnMerge();
        }

        private void OnNotifyGlobalMapCombatResultsShown(long receivedFrom, NotifyGlobalMapCombatResultsShown message)
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapCombatResults, message.PlayerId);

            UpdateGlobalMapCombatResultsUIState();
        }

        private void OnNotifyCrusadeArmyBattleResultsShown(long receivedFrom, NotifyCrusadeArmyBattleResultsShown message)
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapCrusadeArmyBattleResults, message.PlayerId);

            UpdateGlobalMapCrusadeArmyBattleResultsUIState();
        }

         private void OnNotifyTacticalCombatTurnInitialized(long receivedFrom, NotifyTacticalCombatTurnInitialized message)
            => SafeFireAndForget(() => OnNotifyTacticalCombatTurnInitializedAsync(receivedFrom, message));

         private async Task OnNotifyTacticalCombatTurnInitializedAsync(long receivedFrom, NotifyTacticalCombatTurnInitialized message)
        {
            await WaitWhileTrue(() => Game.ArmyCombat == null || Game.ArmyCombat.Seed == 0 || !CombatInteraction.IsInCrusadeTacticalCombat(), "Crusade army combat has not been started yet");

            AddPlayerCrusadeArmyCombatTurnInitialization(message.TurnNumber, message.PlayerId);
        }

        private void OnNotifyPlayerReadyStatusChanged(long receivedFrom, NotifyPlayerReadyStatusChanged message)
        {
            UpdateReadyStatus(message.PlayerId, message.IsReady);
        }

         private void OnNotifyCutsceneSkipped(long playerId, NotifyCutsceneSkipped message)
            => SafeFireAndForget(() => OnNotifyCutsceneSkippedAsync(playerId, message));

         private async Task OnNotifyCutsceneSkippedAsync(long playerId, NotifyCutsceneSkipped message)
        {
            var isCutscene = await WaitWhileTrue(() => GameInteraction.CurrentGameMode != GameModeType.Cutscene, "Waiting for cutscene to be played before applying skip");
            if (!isCutscene)
            {
                return;
            }

            var player = GetPlayer(message.PlayerId);
            GameInteraction.SkipCutscene(player.Name);
        }

        private void OnNotifyPingedByPlayer(long receivedFrom, NotifyPingedByPlayer message)
        {
            if (Game == null || message?.Ping == null || message.SessionId != Game.Id
                || message.LoadedSaveSeed != Game.LoadedSaveSeed || message.AreaSeed != AreaSeed
                || message.PlayerId == Game.LocalPlayerId) return;
            var ping = Mapper.Map<NetworkPing>(message.Ping);

            var player = GetPlayer(message.PlayerId);
            if (player == null) return;
            PingInteraction.Create(player, ping);
            Logger.LogInformation("Remote map ping received. PlayerId={PlayerId}, AreaSeed={AreaSeed}, Position={Position}",
                player.Id, AreaSeed, ping.WorldPosition);
        }

        private void OnNotifyRestEnded(long receivedFrom, NotifyRestEnded message)
        {
            AddPlayerToTracker(Game.Rest.PlayersFinishedRest, message.PlayerId);

            UpdateRestResultsUIState();
        }

        private void OnNotifyGameModeTypeEnded(long receivedFrom, NotifyGameModeTypeEnded message)
        {
            var gameMode = GameModeType.All.FirstOrDefault(g => string.Equals(g.Name, message.Type, StringComparison.OrdinalIgnoreCase));
            UnregisterGameMode(gameMode, message.PlayerId);
            if (gameMode == GameModeType.Rest)

            {
                OnRemoteRestGameModeEnded(message.PlayerId);
            }
        }

        private void OnNotifyGameModeTypeStarted(long receivedFrom, NotifyGameModeTypeStarted message)
        {
            var gameMode = GameModeType.All.FirstOrDefault(g => string.Equals(g.Name, message.Type, StringComparison.OrdinalIgnoreCase));
            RegisterGameMode(gameMode, message.PlayerId);

            if (gameMode == GameModeType.Rest)
            {
                OnRemoteRestGameModeStarted();
            }
            else if (gameMode == GameModeType.Pause)
            {
                OnRemotePauseGameModeStarted(message.PlayerId);
            }
        }

        private void OnNotifyNewGameSequenceWitnessed(long receivedFrom, NotifyNewGameSequenceWitnessed message)
        {
            var phase = Mapper.Map<NetworkNewGameSequencePhase>(message.Phase);
            WitnessNewGameSequencePhase(message.PlayerId, phase.Type);
        }

        private void OnNotifyCharacterSelectionWindowShown(long receivedFrom, NotifyCharacterSelectionWindowShown message)
        {
            AddPlayerToTracker(Game.PlayersInCharacterSelectionWindow, message.PlayerId);

            UpdateCharacterSelectionUIState();
        }

        private void OnNotifyLevelingRespecMythicLevelUp(long receivedFrom, NotifyLevelingRespecMythicLevelUp message)
        {
            RemovePlayerFromTracker(Game.PlayersInRespecWindow, message.PlayerId);
            LevelingInteraction.InitiateLevelingRespecMythicLevelUp();
        }

        private void OnNotifyLevelingRespecLevelUp(long receivedFrom, NotifyLevelingRespecLevelUp message)
        {
            RemovePlayerFromTracker(Game.PlayersInRespecWindow, message.PlayerId);
            LevelingInteraction.InitiateLevelingRespecLevelUp();
        }

        private void OnNotifyLevelingRespecWindowShown(long receivedFrom, NotifyLevelingRespecWindowShown message)
        {
            AddPlayerToTracker(Game.PlayersInRespecWindow, message.PlayerId);

            UpdateLevelingRespecUIState(message.UnitId);
        }

        private void OnNotifyLevelingRespecCompleted(long receivedFrom, NotifyLevelingRespecCompleted message)
        {
            ResetPlayersTracker(Game.PlayersInRespecWindow);

            LevelingInteraction.CompleteLevelingRespec();
        }

        private void OnNotifyLevelingWarpaintColorAppearanceChanged(long receivedFrom, NotifyLevelingWarpaintColorAppearanceChanged message)
        {
            var levelingWarpaint = Mapper.Map<NetworkLevelingWarpaint>(message.Warpaint);
            LevelingInteraction.SelectLevelingWarpaintColorAppearance(levelingWarpaint);
        }

        private void OnNotifyLevelingWarpaintAppearanceChanged(long receivedFrom, NotifyLevelingWarpaintAppearanceChanged message)
        {
            var levelingWarpaint = Mapper.Map<NetworkLevelingWarpaint>(message.Warpaint);
            LevelingInteraction.SelectLevelingWarpaintAppearance(levelingWarpaint);
        }

        private void OnNotifyLevelingTattooColorAppearanceChanged(long receivedFrom, NotifyLevelingTattooColorAppearanceChanged message)
        {
            var levelingTattoo = Mapper.Map<NetworkLevelingTattoo>(message.Tattoo);
            LevelingInteraction.SelectLevelingTattooColorAppearance(levelingTattoo);
        }

        private void OnNotifyLevelingTattooAppearanceChanged(long receivedFrom, NotifyLevelingTattooAppearanceChanged message)
        {
            var levelingTattoo = Mapper.Map<NetworkLevelingTattoo>(message.Tattoo);
            LevelingInteraction.SelectLevelingTattooAppearance(levelingTattoo);
        }

        private void OnNotifyLevelingScarAppearanceChanged(long receivedFrom, NotifyLevelingScarAppearanceChanged message)
        {
            LevelingInteraction.SelectLevelingScarAppearance(message.Index);
        }

        private void OnNotifyLevelingSecondaryOutfitColorAppearanceChanged(long receivedFrom, NotifyLevelingSecondaryOutfitColorAppearanceChanged message)
        {
            LevelingInteraction.SelectLevelingSecondaryOutfitColorAppearance(message.TextureName);
        }

        private void OnNotifyLevelingPrimaryOutfitColorAppearanceChanged(long receivedFrom, NotifyLevelingPrimaryOutfitColorAppearanceChanged message)
        {
            LevelingInteraction.SelectLevelingPrimaryOutfitColorAppearance(message.TextureName);
        }

        private void OnNotifyLevelingHornsColorAppearanceChanged(long receivedFrom, NotifyLevelingHornsColorAppearanceChanged message)
        {
            LevelingInteraction.SelectLevelingHornsColorAppearance(message.TextureName);
        }

        private void OnNotifyLevelingHornsAppearanceChanged(long receivedFrom, NotifyLevelingHornsAppearanceChanged message)
        {
            LevelingInteraction.SelectLevelingHornsAppearance(message.Index);
        }

        private void OnNotifyLevelingHairStyleAppearanceChanged(long receivedFrom, NotifyLevelingHairStyleAppearanceChanged message)
        {
            LevelingInteraction.SelectLevelingHairStyleAppearance(message.Index);
        }

        private void OnNotifyLevelingHairColorAppearanceChanged(long receivedFrom, NotifyLevelingHairColorAppearanceChanged message)
        {
            LevelingInteraction.SelectLevelingHairColorAppearance(message.TextureName);
        }

        private void OnNotifyLevelingFaceAppearanceChanged(long receivedFrom, NotifyLevelingFaceAppearanceChanged message)
        {
            LevelingInteraction.SelectLevelingFaceAppearance(message.Index);
        }

        private void OnNotifyLevelingEyesColorAppearanceChanged(long receivedFrom, NotifyLevelingEyesColorAppearanceChanged message)
        {
            LevelingInteraction.SelectLevelingEyesColorAppearance(message.TextureName);
        }

        private void OnNotifyLevelingBodyColorAppearanceChanged(long receivedFrom, NotifyLevelingBodyColorAppearanceChanged message)
        {
            LevelingInteraction.SelectLevelingBodyColorAppearance(message.TextureName);
        }

        private void OnNotifyLevelingBodyTypeAppearanceChanged(long receivedFrom, NotifyLevelingBodyTypeAppearanceChanged message)
        {
            LevelingInteraction.SelectLevelingBodyTypeAppearance(message.Index);
        }

        private void OnNotifyDialogPopupShown(long receivedFrom, NotifyDialogPopupShown message)
        {
            AddPlayerToTracker(Game.PlayersInDialogPopup, message.PlayerId);

            UpdateDialogPopupState();
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

        private void OnNotifyInventoryItemTransferred(long receivedFrom, NotifyInventoryItemTransferred message)
        {
            var transferItem = Mapper.Map<NetworkItemsTransfer>(message.TransferItem);
            GameInteraction.TransferInventoryItems(transferItem);
        }

        private void OnNotifyZoneLootClosed(long receivedFrom, NotifyZoneLootClosed message)
        {
            RemovePlayerFromTracker(Game.PlayersInZoneLoot, message.PlayerId);
            UpdateZoneLootUIState();
        }

         private void OnNotifyZoneLootShown(long receivedFrom, NotifyZoneLootShown message)
            => SafeFireAndForget(() => OnNotifyZoneLootShownAsync(receivedFrom, message));

         private async Task OnNotifyZoneLootShownAsync(long receivedFrom, NotifyZoneLootShown message)
        {
            AddPlayerToTracker(Game.PlayersInZoneLoot, message.PlayerId);
            await GameInteraction.ShowZoneLootAsync(message.AreaExitId);
            UpdateZoneLootUIState();
        }

        private void OnNotifyGlobalMapEncounterMessageShown(long receivedFrom, NotifyGlobalMapEncounterMessageShown message)
        {
            AddPlayerToTracker(Game.PlayersInGlobalMapEncounterMessage, message.PlayerId);
            UpdateGlobalMapEncounterMessageUIState();
        }

        private void OnNotifyGroupChangerOpened(long receivedFrom, NotifyGroupChangerOpened message)
        {
            AddPlayerToTracker(Game.PlayersInGroupChanger, message.PlayerId);
            UpdateGroupManagerUIState();
        }

        private void OnNotifyGlobalMapLocationMessageShown(long receivedFrom, NotifyGlobalMapLocationMessageShown message)
        {
            if (!GlobalMapPresentationScope.Matches(message.Scope)) return;
            AddPlayerToTracker(Game.PlayersInGlobalMapLocationMessage, message.PlayerId);
            if (message.Location != null && GetPlayer(message.PlayerId)?.IsHost == true)
            {
                GlobalMapInteraction.ShowReceivedLocation(Mapper.Map<NetworkGlobalMapLocation>(message.Location));
            }
            else if (message.ShouldManuallyTriggerMessage)
            {
                GlobalMapInteraction.ShowCurrentEnterCurrentLocationMessage();
            }

            UpdateGlobalMapLocationMessageUIState();
        }

        private void OnNotifySkipTimeOpened(long receivedFrom, NotifySkipTimeOpened message)
        {
            AddPlayerToTracker(Game.PlayersInSkipTime, message.PlayerId);
            GameInteraction.OpenSkipTimeUI();

            UpdateSkipTimeUIState();
        }

        private void OnNotifyUnitStealthChanged(long receivedFrom, NotifyUnitStealthChanged message)
        {
            GameInteraction.ChangeUnitStealth(message.UnitId, message.IsEnabled, message.IsForced);
        }

        private void OnNotifyCombatTurnDelayed(long receivedFrom, NotifyCombatTurnDelayed message)
        {
            PlayerNotification.AddCombatText(WellKnownKeys.GameNotifications.Combat.Turn.Delayed.Key, CombatTextSeverity.Common, new UnitLogParameter(message.UnitId));

            CombatInteraction.DelayCombatTurn(message.UnitId, message.TargetUnitId);
        }

        private void OnNotifyMapObjectLockpicked(long receivedFrom, NotifyMapObjectLockpicked message)
        {
            var lockpickInteraction = Mapper.Map<NetworkLockpickInteraction>(message.LockpickInteraction);

            GameInteraction.LockpickMapObject(lockpickInteraction);
        }

        private void OnNotifyActionBarSlotMoved(long receivedFrom, NotifyActionBarSlotMoved message)
        {
            var sourceActionBarSlot = Mapper.Map<NetworkActionBarSlot>(message.SourceActionBarSlot);
            var targetActionBarSlot = Mapper.Map<NetworkActionBarSlot>(message.TargetActionBarSlot);

            GameInteraction.MoveActionBarSlots(sourceActionBarSlot, targetActionBarSlot);
        }

        private void OnNotifyActionBarSlotCleared(long receivedFrom, NotifyActionBarSlotCleared message)
        {
            var actionBarSlot = Mapper.Map<NetworkActionBarSlot>(message.ActionBarSlot);

            GameInteraction.ClearActionBarSlot(actionBarSlot);
        }

        private void OnNotifyMapObjectClicked(long receivedFrom, NotifyMapObjectClicked message)
        {
            var click = Mapper.Map<NetworkClick>(message.Click);
            GameInteraction.ClickMapObject(click);
        }

        private void OnNotifyToggleActivatableAbility(long receivedFrom, NotifyToggleActivatableAbility message)
        {
            var ability = Mapper.Map<NetworkActivatableAbility>(message.Ability);
            GameInteraction.ToggleActivatableAbility(ability);
        }

        private void OnNotifyAbilityUsed(long receivedFrom, NotifyAbilityUsed message)
        {
            var ability = Mapper.Map<NetworkAbilityUse>(message.AbilityUse);
            CombatInteraction.UseAbility(ability);
        }

        private void OnNotifyUnitAttacked(long receivedFrom, NotifyUnitAttacked message)
        {
            var attack = Mapper.Map<NetworkUnitAttack>(message.Attack);
            CombatInteraction.AttackUnit(attack);
        }

        private void OnNotifyActiveHandEquipmentSetChanged(long receivedFrom, NotifyActiveHandEquipmentSetChanged message)
        {
            var set = Mapper.Map<NetworkActiveHandEquipmentSet>(message.Set);
            GameInteraction.SetActiveHandEquipmentSet(set);
        }

        private void OnNotifyEquipmentSlotChanged(long receivedFrom, NotifyEquipmentSlotChanged message)
        {
            var slot = Mapper.Map<NetworkEquipmentSlot>(message.Slot);
            GameInteraction.UpdateEquipmentSlot(slot);
        }

        private void OnNotifyDropItem(long receivedFrom, NotifyDropItem message)
        {
            var dropItem = Mapper.Map<NetworkDropItem>(message.Drop);
            GameInteraction.DropItem(dropItem);
        }

        private void OnNotifyInventoryItemUsed(long receivedFrom, NotifyInventoryItemUsed message)
        {
            var useItem = Mapper.Map<NetworkUseInventoryItem>(message.UseItem);
            GameInteraction.UseInventoryItem(useItem);
        }

        private void OnNotifyContainerSkinned(long receivedFrom, NotifyLootableEntitySkinned message)
        {
            var container = Mapper.Map<NetworkLootableEntity>(message.Entity);
            GameInteraction.SkinLootContainer(container);
        }

        private void OnNotifyOvertipInteracted(long receivedFrom, NotifyOvertipInteracted message)
        {
            var overtip = Mapper.Map<NetworkOvertip>(message.Overtip);
            GameInteraction.InteractWithOvertip(overtip);
        }

        private void OnNotifyUnitJoinedMidCombat(long receivedFrom, NotifyUnitJoinedMidCombat message)
        {
            if (Game.Combat == null)
            {
                return;
            }

            AddPlayerReadyStatus(PlayerTurnReadinessType.UnitJoinedMidCombat, message.PlayerId, message.UnitId);
        }

        private void OnNotifyRestBanterInterrupted(long receivedFrom, NotifyRestBanterInterrupted message)
        {
            var banter = Mapper.Map<NetworkRestBanter>(message.Banter);
            GameInteraction.TryInterruptRestBanter(banter);
        }

        private void OnNotifyVendorWindowClosed(long receivedFrom, NotifyVendorWindowClosed message)
        {
            GameInteraction.CloseVendorWindow();
        }

        private void OnNotifyVendorDealMade(long receivedFrom, NotifyVendorDealMade message)
        {
            GameInteraction.MakeVendorDeal();
        }

        private void OnNotifyVendorItemTransferred(long receivedFrom, NotifyVendorItemTransferred message)
        {
            var transfer = Mapper.Map<NetworkVendorItemTransfer>(message.ItemTransfer);
            GameInteraction.TransferVendorItem(transfer);
        }

        private void OnNotifySpellForgotten(long receivedFrom, NotifySpellForgotten message)
        {
            var slot = Mapper.Map<NetworkSpellSlot>(message.Slot);
            var ability = Mapper.Map<NetworkAbility>(message.Ability);

            GameInteraction.ForgetSpell(message.UnitId, slot, ability);
        }

        private void OnNotifySpellMemorized(long receivedFrom, NotifySpellMemorized message)
        {
            var slot = Mapper.Map<NetworkSpellSlot>(message.Slot);
            var ability = Mapper.Map<NetworkAbility>(message.Ability);

            GameInteraction.MemorizeSpell(message.UnitId, slot, ability);
        }

        private void OnNotifyLevelingPortraitSelected(long receivedFrom, NotifyLevelingPortraitSelected message)
        {
            var levelingPortrait = Mapper.Map<NetworkLevelingPortrait>(message.Portrait);
            LevelingInteraction.SelectLevelingPortrait(levelingPortrait);
        }

        private void OnNotifyLevelingVoiceSelected(long receivedFrom, NotifyLevelingVoiceSelected message)
        {
            var levelingVoice = Mapper.Map<NetworkLevelingVoice>(message.Voice);
            LevelingInteraction.SelectLevelingVoice(levelingVoice);
        }

        private void OnNotifyLevelingAlignmentSelected(long receivedFrom, NotifyLevelingAlignmentSelected message)
        {
            LevelingInteraction.SelectLevelingAlignment(message.AlignmentId);
        }

        private void OnNotifyLevelingNameChanged(long receivedFrom, NotifyLevelingNameChanged message)
        {
            LevelingInteraction.SetLevelingName(message.Name);
        }

        private void OnNotifyLevelingGenderSelected(long receivedFrom, NotifyLevelingGenderSelected message)
        {
            LevelingInteraction.SelectLevelingGender(message.GenderId);
        }

        private void OnNotifyLevelingRaceSelected(long receivedFrom, NotifyLevelingRaceSelected message)
        {
            LevelingInteraction.SelectLevelingRace(message.RaceId);
        }

        private void OnNotifyLevelingRacialAbilityScoreBonusChanged(long receivedFrom, NotifyLevelingRacialAbilityScoreBonusChanged message)
        {
            var direction = Mapper.Map<NetworkLevelingSequenceDirection>(message.Direction);

            LevelingInteraction.ChangeLevelingRacialAbilityScoreBonus(direction);
        }

        private void OnNotifyLevelingBirthDayChanged(long receivedFrom, NotifyLevelingBirthDayChanged message)
        {
            var direction = Mapper.Map<NetworkLevelingSequenceDirection>(message.Direction);

            LevelingInteraction.ChangeLevelingBirthDay(direction);
        }

        private void OnNotifyLevelingBirthMonthChanged(long receivedFrom, NotifyLevelingBirthMonthChanged message)
        {
            var direction = Mapper.Map<NetworkLevelingSequenceDirection>(message.Direction);

            LevelingInteraction.ChangeLevelingBirthMonth(direction);
        }

        private void OnNotifyLevelingAbilityScoreDecreased(long receivedFrom, NotifyLevelingAbilityScoreDecreased message)
        {
            var abilityScore = Mapper.Map<NetworkLevelingAbilityScore>(message.AbilityScore);
            LevelingInteraction.DecreaseLevelingAbilityScore(abilityScore);
        }

        private void OnNotifyLevelingAbilityScoreIncreased(long receivedFrom, NotifyLevelingAbilityScoreIncreased message)
        {
            var abilityScore = Mapper.Map<NetworkLevelingAbilityScore>(message.AbilityScore);
            LevelingInteraction.IncreaseLevelingAbilityScore(abilityScore);
        }

        private void OnNotifyLevelingCompleted(long receivedFrom, NotifyLevelingCompleted message)
        {
            LevelingInteraction.CompleteLeveling();
        }

        private void OnNotifyLevelingTerminated(long receivedFrom, NotifyLevelingTerminated message)
        {
            LevelingInteraction.TerminateLeveling();
        }

        private void OnNotifyLevelingSpellRemoved(long receivedFrom, NotifyLevelingSpellRemoved message)
        {
            var spell = Mapper.Map<NetworkLevelingSpell>(message.Spell);
            LevelingInteraction.RemoveLevelingSpell(spell);
        }

        private void OnNotifyLevelingSpellChosen(long receivedFrom, NotifyLevelingSpellChosen message)
        {
            var spell = Mapper.Map<NetworkLevelingSpell>(message.Spell);
            LevelingInteraction.SelectLevelingSpell(spell);
        }

        private void OnNotifyLevelingFeatureSelected(long receivedFrom, NotifyLevelingFeatureSelected message)
        {
            var feature = Mapper.Map<NetworkLevelingFeature>(message.Feature);
            LevelingInteraction.SelectLevelingFeature(feature);
        }

        private void OnNotifyLevelingSkillPointDecreased(long receivedFrom, NotifyLevelingSkillPointDecreased message)
        {
            var skillPoint = Mapper.Map<NetworkLevelingSkillPoint>(message.Skill);
            LevelingInteraction.DecreaseLevelingSkillPoint(skillPoint);
        }

        private void OnNotifyLevelingSkillPointIncreased(long receivedFrom, NotifyLevelingSkillPointIncreased message)
        {
            var skillPoint = Mapper.Map<NetworkLevelingSkillPoint>(message.Skill);
            LevelingInteraction.IncreaseLevelingSkillPoint(skillPoint);
        }

        private void OnNotifyLevelingPhaseChanged(long receivedFrom, NotifyLevelingPhaseChanged message)
        {
            var phase = Mapper.Map<NetworkLevelingPhase>(message.Phase);
            ResetPlayersTracker(Game.Leveling.PlayerReadiness);
            LevelingInteraction.SwitchLevelingPhase(phase);
        }

         private void OnNotifyLevelingPhaseWitnessed(long receivedFrom, NotifyLevelingPhaseWitnessed message)
            => SafeFireAndForget(() => OnNotifyLevelingPhaseWitnessedAsync(receivedFrom, message));

         private async Task OnNotifyLevelingPhaseWitnessedAsync(long receivedFrom, NotifyLevelingPhaseWitnessed message)
        {
            var session = Game;
            // leveling is always created at the host first and later on on clients as a part of leveling confirmation
            // but not in case when game is forcing leveling ui to open for everyone at the same time => means there is some racing there
            await WaitWhileTrue(() => Game.Leveling == null, "Received leveling witness notification, but leveling has not been started yet.");

            if (!ReferenceEquals(Game, session) || !IsCurrentLevelingIdentity(message.Leveling)
                || !SameLevelingIdentity(Game?.Leveling?.Identity, message.Leveling)) return;
            WitnessLevelingPhase(message.PlayerId);
        }

        private void OnNotifyLevelingClassArchetypeSelected(long receivedFrom, NotifyLevelingClassArchetypeSelected message)
        {
            var archetype = Mapper.Map<NetworkLevelingArchetype>(message.Archetype);
            LevelingInteraction.SelectLevelingClassArchetype(archetype);
        }

        private void OnNotifyLevelingMythicClassSelected(long receivedFrom, NotifyLevelingMythicClassSelected message)
        {
            LevelingInteraction.SelectMythicLevelingClass(message.MythicClassId);
        }

        private void OnNotifyLevelingClassSelected(long receivedFrom, NotifyLevelingClassSelected message)
        {
            var levelingClass = Mapper.Map<NetworkLevelingClass>(message.Class);
            LevelingInteraction.SelectLevelingClass(levelingClass);
        }

        private ConcurrentDictionary<string, HashSet<long>> GetPlayerTurnReadinessTracker(PlayerTurnReadinessType type)
        {
            var tracker = type switch
            {
                PlayerTurnReadinessType.Start => Game.Combat.PlayersNextTurnInitialization,
                PlayerTurnReadinessType.UnitSynchronization => Game.Combat.PlayersNextTurnSynchronization,
                PlayerTurnReadinessType.UnitJoinedMidCombat => Game.Combat.MidCombatUnitJoins,
                _ => null,
            };

            return tracker;
        }

        private void UpdateConfirmedMidCombatUnits()
        {
            lock (ActionLock)
            {
                var midCombat = GetPlayerTurnReadinessTracker(PlayerTurnReadinessType.UnitJoinedMidCombat);
                foreach (var kv in midCombat)
                {
                    if (kv.Value.Count >= GetSyncedPlayersCount())
                    {
                        Game.Combat.ConfirmedMidCombatUnits.Add(kv.Key);
                    }
                }
            }
        }

        private void InitializeNewTurn(string unitId, bool actingInSurpriseRound)
        {
            lock (ActionLock)
            {
                Game.Combat.Turn = new NetworkCombatTurn
                {
                    UnitId = unitId,
                    Stage = NetworkCombatTurnStage.Initialization,
                    IsActingInSurpriseRound = actingInSurpriseRound,
                    IsLocalPlayer = IsControlledByLocalPlayer(unitId),
                    IsAI = !GameInteraction.IsUnitInParty(unitId),
                };
            }

            Logger.LogInformation("Turn has been initialized. UnitId={UnitId}, IsLocalPlayer={IsLocalPlayer}, IsAI={IsAI}, IsActingInSurpriseRound={IsActingInSurpriseRound}, Stage={Stage}",
                unitId, Game.Combat.Turn.IsLocalPlayer, Game.Combat.Turn.IsAI, Game.Combat.Turn.IsActingInSurpriseRound, Game.Combat.Turn.Stage);

            OnLocalPlayerTurnStart();
        }

        private bool WasControlledByCurrentPlayer(string unitId)
        {
            var realCharacterUnitId = GameInteraction.GetPetOwnerId(unitId) ?? unitId;

            if (string.IsNullOrEmpty(realCharacterUnitId)
                || !Game.CharactersOwnershipHistory.TryGetValue(realCharacterUnitId, out var playerId)
                || GetPlayer(playerId) == null)
            {
                return HasControlOverUI;
            }

            var canControl = playerId == Game.LocalPlayerId;
            return canControl;
        }

        private bool ReadyChanged(NetworkPlayer networkPlayer, bool isReady)
        {
            lock (ActionLock)
            {
                networkPlayer.IsReady = isReady;

                InvokeOnPlayersChanged();

                var readyChanged = new NotifyPlayerReadyStatusChanged { PlayerId = networkPlayer.Id, IsReady = networkPlayer.IsReady };
                Send(readyChanged);

                return networkPlayer.IsReady;
            }
        }

        private void SaveLastCombatTurn()
        {
            if (Game.Combat?.Turn != null)
            {
                Game.LastCombatTurn = Game.Combat.Turn;
            }
        }

        private void MakeUnitTargetable(string unitId, string groupId)
        {
            if (Game.Combat == null)
            {
                return;
            }

            if (!Game.Combat.UntargetableUnits.TryGetValue(groupId, out var group))
            {
                Logger.LogWarning("Group has no untargetable units. GroupId={GroupId}", groupId);
                return;
            }

            if (group.Remove(unitId))
            {
                var isLastGroupMember = group.Count == 0;
                if (isLastGroupMember)
                {
                    Game.Combat.UntargetableUnits.TryRemove(groupId, out _);
                    Logger.LogWarning("Unit was last in untargetable group. UnitId={UnitId}, GroupId={GroupId}", unitId, groupId);
                }
                CombatInteraction.MakeUnitTargetable(unitId);
            }
        }

        private void ResetOnSaveLoad()
        {
            Logger.LogInformation("Reset on save load");

            ResetGameIdGenerator();
            ResetGlobalMapCounters();
            GlobalMapInteraction.EnableInteractions();
            Game.DialogState = null;

            ResetPlayersTracker(Game.PlayersInGroupChanger);
            ResetPlayersTracker(Game.PlayersInRespecWindow);
        }

        private void MakeUnitUntargetable(string unitId, string groupId)
        {
            if (Game.Combat == null)
            {
                return;
            }

            var group = Game.Combat.UntargetableUnits.GetOrAdd(groupId, []);
            if (group.Add(unitId))
            {
                CombatInteraction.MakeUnitUntargetable(unitId);
            }
        }

        private void IncludeSeed(StringBuilder idBuilder, SeedKind seedConfig, SeedKind seed)
        {
            var value = (seedConfig & seed) != 0 ? (GetSeedValue(seed) ?? 0).ToString() : "excluded";
            if (idBuilder.Length > 0)
            {
                idBuilder.Append(',');
            }

            idBuilder.Append(seed).Append('=').Append(value);
        }

        private int? GetSeedValue(SeedKind seedKind)
        {
            if (_combatSaveSemanticSeeds != null && Game.Combat != null)
            {
                var key = seedKind switch { SeedKind.Session => "session", SeedKind.LoadedSaveSeed => "load",
                    SeedKind.AreaSeed => "area", _ => null };
                if (key != null && _combatSaveSemanticSeeds.TryGetValue(key, out var saved)) return saved;
            }
            return seedKind switch
            {
                SeedKind.Session => SessionSeed,
                SeedKind.LoadedSaveSeed => LoadedSaveSeed,
                SeedKind.AreaSeed => AreaSeed,
                SeedKind.CombatSeed => CombatSeed,
                SeedKind.CombatTurnSeed => CombatTurnSeed,
                SeedKind.CrusadeArmyCombatAreaSeed => CrusadeArmyCombatAreaSeed,
                SeedKind.CrusadeArmyCombatSeed => CrusadeArmyCombatSeed,
                _ => null
            };
        }

        private void OnUnitDeathInCombat(string unitId, string groupId)
        {
            if (Game.RealTimeCombat?.Mode == CombatMode.RealTimeWithPause)
            {
                // Presentation clients consume sequenced absolute UnitResult
                // events/checkpoints, never the old callback-driven kill lane.
                if (RealTimePresentationRuntime.Enabled) return;
                // The turn-based notification below has no turn to attach to in RTwP.
                // Only the host publishes irreversible death; a client's local rule
                // simulation may disagree and must not kill a host-owned unit.
                if (Game.Combat?.Stage == NetworkCombatStage.Running
                    && Game.LocalPlayerId == Networking.NetworkConstants.HostPlayerId)
                {
                    var state = Game.RealTimeCombat;
                    var epoch = state.Phase == CombatSynchronizationPhase.PreparingModeSwitch
                        && state.PreviousMode == CombatMode.RealTimeWithPause
                        ? state.Epoch - 1 : state.Epoch;
                    Send(new NotifyRealTimeUnitKilled
                    {
                        SessionId = Game.Id,
                        LoadedSaveSeed = Game.LoadedSaveSeed,
                        Epoch = epoch,
                        Sequence = Interlocked.Increment(ref _nextRealTimeUnitDeathSequence),
                        UnitId = unitId
                    });
                }
                return;
            }
            // The host owns irreversible combat results in the presentation build,
            // including after returning to native turn-based mode.  A client's native
            // controller may replay death callbacks for stale same-area corpses while
            // rebuilding its roster; forwarding those callbacks can end only the
            // client's combat and poison the host state.
            if (!CanPublishLegacyTurnBasedDeath(Game.LocalPlayerId))
                return;
            if (Game.Combat?.Turn == null)
            {
                return;
            }

            if (Game.Combat.UntargetableUnits.ContainsKey(groupId))
            {
                // if unit is dead for some reason
                MakeUnitTargetable(unitId, groupId);
            }

            OnUnitDeath(unitId, Game.Combat.RemotelyKilledUnits, CombatInteraction.IsRiderActive);
        }

        internal static bool CanPublishLegacyTurnBasedDeath(long localPlayerId) =>
            !RealTimePresentationRuntime.Enabled || localPlayerId == Networking.NetworkConstants.HostPlayerId;

        private void OnUnitDeathInTacticalCombat(string unitId)
        {
            OnUnitDeath(unitId, Game.ArmyCombat.RemotelyKilledUnits, CombatInteraction.IsActiveArmyCombatUnitBusy);
        }

        private void OnUnitDeath(string unitId, HashSet<string> remotelyKilledUnits, Func<bool> awaiter)
        {
            if (remotelyKilledUnits.Contains(unitId))
            {
                return;
            }

            var message = new NotifyUnitKilled
            {
                PlayerId = Game.LocalPlayerId,
                UnitId = unitId
            };

            Task.Run(async () =>
            {
                await WaitWhileTrue(awaiter, "Waiting for an active unit to finish acting before sending unit killed notification");
                var settings = SettingsService.GetSettings();
                if (settings.LocalUnitDeathNotificationDelay > TimeSpan.Zero)
                {
                    await Task.Delay(settings.LocalUnitDeathNotificationDelay);
                }

                Send(message);
            });
        }
    }
}
