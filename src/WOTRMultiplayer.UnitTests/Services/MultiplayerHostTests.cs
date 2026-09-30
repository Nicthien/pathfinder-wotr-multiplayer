using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using FakeItEasy;
using Kingmaker.GameModes;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Abstractions.Hashing;
using WOTRMultiplayer.Abstractions.IO;
using WOTRMultiplayer.Abstractions.Random;
using WOTRMultiplayer.Abstractions.Settings;
using WOTRMultiplayer.Abstractions.Unity;
using WOTRMultiplayer.Config.Mapping;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.Crusades;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Content;
using WOTRMultiplayer.Entities.Items;
using WOTRMultiplayer.Entities.Settings;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Abstractions;
using WOTRMultiplayer.Networking.Configuration;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Networking.Messages.Lobby;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.Random;
using WOTRMultiplayer.UnitTests.FakeRules;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class MultiplayerHostTests
    {
        private MultiplayerHost _multiplayerHost;

        private ILogger<MultiplayerHost> _logger;
        private IGameInteractionService _gameInteractionService;
        private ILevelingInteractionService _levelingInteractionService;
        private IPlayerNotificationService _playerNotificationService;
        private IDialogInteractionService _dialogInteractionService;
        private IGlobalMapInteractionService _globalMapInteractionService;
        private IPingInteractionService _pingInteractionService;
        private ICombatInteractionService _combatInteractionService;
        private IMultiplayerSettingsService _multiplayerSettingsProvider;
        private IFileSystemService _fileSystemService;
        private IHashService _hashService;
        private INetworkHostConnection _networkServer;
        private IValueGenerator _valueGenerator;
        private IMapper _mapper;
        private IMainThreadAccessor _mainThreadAccessor;

        [Test]
        public void RealTimeSynchronizationBarrier_BlocksSaving()
        {
            A.CallTo(() => _networkServer.IsActive).Returns(true);
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                RealTimeCombat = new NetworkRealTimeCombatState { Phase = CombatSynchronizationPhase.Repairing }
            };

            Assert.That(_multiplayerHost.IsSavingBlockedByCombatSync, Is.True);
            _multiplayerHost.Game.RealTimeCombat.Phase = CombatSynchronizationPhase.Stable;
            Assert.That(_multiplayerHost.IsSavingBlockedByCombatSync, Is.False);
        }

        [Test]
        public void RunningRealTimeCombat_BlocksSavingUntilTurnBasedOrCombatEnd()
        {
            A.CallTo(() => _networkServer.IsActive).Returns(true);
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause }
            };

            Assert.That(_multiplayerHost.IsSavingBlockedByCombatSync, Is.True);
            _multiplayerHost.Game.RealTimeCombat.Mode = CombatMode.TurnBased;
            Assert.That(_multiplayerHost.IsSavingBlockedByCombatSync, Is.False);
        }

        [SetUp]
        public void SetUp()
        {
            _mapper = new MapperConfiguration(x =>
            {
                x.AddProfile<NetworkingProfile>();
                x.AddProfile<NetworkMessagesProfile>();
            }).CreateMapper();

            _logger = A.Fake<ILogger<MultiplayerHost>>();
            _gameInteractionService = A.Fake<IGameInteractionService>();
            _levelingInteractionService = A.Fake<ILevelingInteractionService>();
            _playerNotificationService = A.Fake<IPlayerNotificationService>();
            _dialogInteractionService = A.Fake<IDialogInteractionService>();
            _globalMapInteractionService = A.Fake<IGlobalMapInteractionService>();
            _pingInteractionService = A.Fake<IPingInteractionService>();
            _combatInteractionService = A.Fake<ICombatInteractionService>();
            _multiplayerSettingsProvider = A.Fake<IMultiplayerSettingsService>();
            _fileSystemService = A.Fake<IFileSystemService>();
            _hashService = A.Fake<IHashService>();
            _mainThreadAccessor = A.Fake<IMainThreadAccessor>();
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes(call => ((Action)call.Arguments[0])());

            _networkServer = A.Fake<INetworkHostConnection>();
            Fake.GetFakeManager(_networkServer).AddRuleFirst(new NetworkReceiverFakeRule<INetworkConnection>());

            _valueGenerator = A.Fake<IValueGenerator>();

            _multiplayerHost = new MultiplayerHost(
                _logger,
                _gameInteractionService,
                _levelingInteractionService,
                _playerNotificationService,
                _dialogInteractionService,
                _globalMapInteractionService,
                _pingInteractionService,
                _combatInteractionService,
                _multiplayerSettingsProvider,
                _fileSystemService,
                _networkServer,
                _valueGenerator,
                _hashService,
                _mapper,
                _mainThreadAccessor);
        }

        [Test]
        public void Create_NonEmptySavePathAndGameId_StartsTcpServer()
        {
            // Arrange
            var startUp = new NetworkGameStartUp() { SavePath = Guid.NewGuid().ToString(), IsNewGameSequence = false };
            var gameId = Guid.NewGuid().ToString();
            var settings = new NetworkMultiplayerSettings { Host = "*", UseIPv6 = true, HostPortRangeStart = 123, HostPortRangeEnd = 1234, NetworkAwaiterTimeout = TimeSpan.FromMinutes(1) };
            A.CallTo(() => _multiplayerSettingsProvider.GetSettings()).Returns(settings);

            // Act
            _multiplayerHost.Create(gameId, null, null, startUp);

            // Assert
            A.CallTo(() => _networkServer.HostTcpServer(A<NetworkServerConfiguration>.That.Matches(x => IsSameConfiguration(x, settings))))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void Start_RejectsDifferentRealTimeProtocolBeforeSaveTransfer()
        {
            var startUp = new NetworkGameStartUp { SavePath = Guid.NewGuid().ToString(), IsNewGameSequence = false };
            A.CallTo(() => _multiplayerSettingsProvider.GetSettings()).Returns(new NetworkMultiplayerSettings());
            _multiplayerHost.Create(Guid.NewGuid().ToString(), null, null, startUp);
            _multiplayerHost.Game.Players.Add(new NetworkPlayer(-1)
            {
                IsHost = true,
                ContentState = new NetworkContentState { GameVersion = "2.7", RealTimeProtocolVersion = NetworkRealTimeCombatState.ProtocolVersion }
            });
            _multiplayerHost.Game.Players.Add(new NetworkPlayer(42)
            {
                ContentState = new NetworkContentState { GameVersion = "2.7", RealTimeProtocolVersion = 0 }
            });

            Assert.That(_multiplayerHost.Start(), Is.False);
            Assert.That(_multiplayerHost.Game.Stage, Is.EqualTo(NetworkLobbyStage.Lobby));
        }

        [Test]
        public void RealTimeSnapshot_IsPublishedOnlyDuringStableRealTimeCombat()
        {
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 5 }
            };
            A.CallTo(() => _combatInteractionService.GetRealTimeCombatState()).Returns(new NetworkCombatState());

            _multiplayerHost.PublishRealTimeCombatSnapshot();

            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeCombatSnapshot>.That.Matches(x => x.Epoch == 5 && x.Sequence == 1 && x.State != null)))
                .MustHaveHappenedOnceExactly();
            _multiplayerHost.Game.RealTimeCombat.Phase = CombatSynchronizationPhase.Repairing;
            _multiplayerHost.PublishRealTimeCombatSnapshot();
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeCombatSnapshot>._)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void RealTimeSnapshot_ClientApplicationFailure_StartsPausedRepair()
        {
            PrepareRealTimeRepairTest();
            A.CallTo(() => _combatInteractionService.GetRealTimeCombatState()).Returns(new NetworkCombatState());
            _multiplayerHost.PublishRealTimeCombatSnapshot();
            var handler = FakeUtils.GetNetworkReceiverHandler<ClientRealTimeStateHash>(_networkServer);

            handler.Invoke(42, new ClientRealTimeStateHash
            {
                Epoch = 0, Sequence = 1, Applied = false,
                FailureReason = nameof(InvalidOperationException)
            });

            Assert.That(_multiplayerHost.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeRepair>.That.Matches(x =>
                x.Stage == (int)RealTimeRepairStage.Prepare))).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void RealTimeCommand_AcceptsOwnedMoveAndRejectsDuplicateAndSpoofedSender()
        {
            A.CallTo(() => _combatInteractionService.ExecuteRealTimeCombatCommand(RealTimeCommandKind.Move,
                A<NetworkUnitMoveTo>._, null, null)).Returns(true);
            var owner = new NetworkPlayer(42);
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 5 }
            };
            _multiplayerHost.Game.Players.Add(owner);
            _multiplayerHost.Game.Players.Add(new NetworkPlayer(99));
            _multiplayerHost.Game.Characters.Add(new NetworkCharacter { UnitId = "unit-a", Owner = owner });
            var handler = FakeUtils.GetNetworkReceiverHandler<ClientRealTimeCombatCommand>(_networkServer);
            var command = new ClientRealTimeCombatCommand
            {
                Epoch = 5,
                Sequence = 1,
                PlayerId = 42,
                Kind = (int)RealTimeCommandKind.Move,
                Movement = new Networking.Messages.Contracts.NetworkUnitMoveTo
                {
                    InitiatorUnitId = "unit-a",
                    Destination = new Networking.Messages.Contracts.NetworkVector3(1, 2, 3)
                }
            };

            handler.Invoke(99, command);
            A.CallTo(() => _combatInteractionService.ExecuteRealTimeCombatCommand(A<RealTimeCommandKind>._,
                A<NetworkUnitMoveTo>._, A<NetworkUnitAttack>._, A<NetworkAbilityUse>._)).MustNotHaveHappened();

            handler.Invoke(42, command);
            handler.Invoke(42, command);

            A.CallTo(() => _combatInteractionService.ExecuteRealTimeCombatCommand(RealTimeCommandKind.Move,
                A<NetworkUnitMoveTo>.That.Matches(x => x.InitiatorUnitId == "unit-a"), null, null))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeCombatCommandAccepted>.That.Matches(x => x.Epoch == 5 && x.AuthoritativeSequence == 1 && x.ClientSequence == 1)))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void RealTimeCommand_FailedHostExecution_IsNeverBroadcast()
        {
            var owner = new NetworkPlayer(42);
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 5 }
            };
            _multiplayerHost.Game.Players.Add(owner);
            _multiplayerHost.Game.Characters.Add(new NetworkCharacter { UnitId = "unit-a", Owner = owner });
            var handler = FakeUtils.GetNetworkReceiverHandler<ClientRealTimeCombatCommand>(_networkServer);

            handler.Invoke(42, new ClientRealTimeCombatCommand
            {
                Epoch = 5,
                Sequence = 1,
                PlayerId = 42,
                Kind = (int)RealTimeCommandKind.Move,
                Movement = new Networking.Messages.Contracts.NetworkUnitMoveTo
                {
                    InitiatorUnitId = "unit-a",
                    Destination = new Networking.Messages.Contracts.NetworkVector3(1, 2, 3)
                }
            });

            A.CallTo(() => _combatInteractionService.ExecuteRealTimeCombatCommand(RealTimeCommandKind.Move,
                A<NetworkUnitMoveTo>._, null, null)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeCombatCommandAccepted>._)).MustNotHaveHappened();
            Assert.That(_multiplayerHost.Game.RealTimeCombat.LastAuthoritativeSequence, Is.Zero);
        }

        [Test]
        public void RealTimeCommand_IsNotAcknowledgedBeforeMainThreadExecution()
        {
            Action queuedAction = null;
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes(call => queuedAction = (Action)call.Arguments[0]);
            A.CallTo(() => _combatInteractionService.ExecuteRealTimeCombatCommand(RealTimeCommandKind.Move,
                A<NetworkUnitMoveTo>._, null, null)).Returns(true);
            var owner = new NetworkPlayer(42);
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 5 }
            };
            _multiplayerHost.Game.Players.Add(owner);
            _multiplayerHost.Game.Characters.Add(new NetworkCharacter { UnitId = "unit-a", Owner = owner });
            var handler = FakeUtils.GetNetworkReceiverHandler<ClientRealTimeCombatCommand>(_networkServer);

            handler.Invoke(42, new ClientRealTimeCombatCommand
            {
                Epoch = 5,
                Sequence = 1,
                PlayerId = 42,
                Kind = (int)RealTimeCommandKind.Move,
                Movement = new Networking.Messages.Contracts.NetworkUnitMoveTo
                {
                    InitiatorUnitId = "unit-a",
                    Destination = new Networking.Messages.Contracts.NetworkVector3(1, 2, 3)
                }
            });

            Assert.That(queuedAction, Is.Not.Null);
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeCombatCommandAccepted>._)).MustNotHaveHappened();
            queuedAction();
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeCombatCommandAccepted>._)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task CombatModeSwitch_DoesNotResumeUntilPeerAppliedCommit()
        {
            A.CallTo(() => _networkServer.IsActive).Returns(true);
            A.CallTo(() => _gameInteractionService.SetPauseAsync(true)).Returns(Task.CompletedTask);
            A.CallTo(() => _gameInteractionService.SetTurnBasedCombatModeAsync(false)).Returns(Task.CompletedTask);
            A.CallTo(() => _combatInteractionService.GetRealTimeCombatState()).Returns(new NetworkCombatState());
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = NetworkConstants.HostPlayerId,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running }
            };
            _multiplayerHost.Game.Players.Add(new NetworkPlayer(NetworkConstants.HostPlayerId) { LobbySyncStatus = NetworkLobbySyncStatus.Succeed });
            _multiplayerHost.Game.Players.Add(new NetworkPlayer(42) { LobbySyncStatus = NetworkLobbySyncStatus.Succeed });

            Assert.That(await _multiplayerHost.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Assert.That(_multiplayerHost.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.PreparingModeSwitch));
            var handler = FakeUtils.GetNetworkReceiverHandler<ClientRealTimeModeTransitionAck>(_networkServer);
            handler.Invoke(42, new ClientRealTimeModeTransitionAck
            {
                Epoch = 1, PlayerId = 42, Stage = (int)RealTimeModeTransitionStage.Prepare, Applied = true
            });

            Assert.That(_multiplayerHost.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Synchronizing));
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeModeTransition>.That.Matches(x => x.Stage == (int)RealTimeModeTransitionStage.Complete)))
                .MustNotHaveHappened();
            handler.Invoke(42, new ClientRealTimeModeTransitionAck
            {
                Epoch = 1, PlayerId = 42, Stage = (int)RealTimeModeTransitionStage.Commit,
                Applied = true, CanonicalStateHash = _multiplayerHost.Game.RealTimeCombat.CanonicalStateHash
            });

            Assert.That(_multiplayerHost.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(_multiplayerHost.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.RealTimeWithPause));
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeModeTransition>.That.Matches(x => x.Stage == (int)RealTimeModeTransitionStage.Complete)))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task CombatModeSwitch_HashMismatch_RollsBackThroughPausedAbort()
        {
            A.CallTo(() => _networkServer.IsActive).Returns(true);
            A.CallTo(() => _gameInteractionService.SetPauseAsync(true)).Returns(Task.CompletedTask);
            A.CallTo(() => _gameInteractionService.SetTurnBasedCombatModeAsync(A<bool>._)).Returns(Task.CompletedTask);
            A.CallTo(() => _combatInteractionService.GetRealTimeCombatState()).Returns(new NetworkCombatState());
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = NetworkConstants.HostPlayerId,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running }
            };
            _multiplayerHost.Game.Players.Add(new NetworkPlayer(NetworkConstants.HostPlayerId) { LobbySyncStatus = NetworkLobbySyncStatus.Succeed });
            _multiplayerHost.Game.Players.Add(new NetworkPlayer(42) { LobbySyncStatus = NetworkLobbySyncStatus.Succeed });
            Assert.That(await _multiplayerHost.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            var handler = FakeUtils.GetNetworkReceiverHandler<ClientRealTimeModeTransitionAck>(_networkServer);
            handler.Invoke(42, new ClientRealTimeModeTransitionAck
            {
                Epoch = 1, PlayerId = 42, Stage = (int)RealTimeModeTransitionStage.Prepare, Applied = true
            });
            handler.Invoke(42, new ClientRealTimeModeTransitionAck
            {
                Epoch = 1, PlayerId = 42, Stage = (int)RealTimeModeTransitionStage.Commit,
                Applied = true, CanonicalStateHash = 1UL
            });

            Assert.That(_multiplayerHost.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
            Assert.That(_multiplayerHost.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeModeTransition>.That.Matches(x => x.Stage == (int)RealTimeModeTransitionStage.Abort)))
                .MustHaveHappenedOnceExactly();
            handler.Invoke(42, new ClientRealTimeModeTransitionAck
            {
                Epoch = 1, PlayerId = 42, Stage = (int)RealTimeModeTransitionStage.Abort,
                Applied = true, CanonicalStateHash = _multiplayerHost.Game.RealTimeCombat.CanonicalStateHash
            });
            Assert.That(_multiplayerHost.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
        }

        [Test]
        public async Task RealTimeRepair_PausedVerificationMatch_ResumesWithoutApplyingSnapshot()
        {
            PrepareRealTimeRepairTest();
            var handler = FakeUtils.GetNetworkReceiverHandler<ClientRealTimeRepairAck>(_networkServer);

            await _multiplayerHost.BeginRealTimeRepairAsync();
            Assert.That(_multiplayerHost.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            handler.Invoke(42, RepairAck(1, RealTimeRepairStage.Prepare, 0));
            var hash = _multiplayerHost.Game.RealTimeCombat.CanonicalStateHash;
            handler.Invoke(42, RepairAck(1, RealTimeRepairStage.Verify, hash));

            Assert.That(_multiplayerHost.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(_multiplayerHost.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.RealTimeWithPause));
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeRepair>.That.Matches(x => x.Stage == (int)RealTimeRepairStage.Apply)))
                .MustNotHaveHappened();
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeRepair>.That.Matches(x => x.Stage == (int)RealTimeRepairStage.Resume)))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task RealTimeRepair_MismatchThenMatchingSnapshot_ResumesRealTime()
        {
            PrepareRealTimeRepairTest();
            var handler = FakeUtils.GetNetworkReceiverHandler<ClientRealTimeRepairAck>(_networkServer);

            await _multiplayerHost.BeginRealTimeRepairAsync();
            handler.Invoke(42, RepairAck(1, RealTimeRepairStage.Prepare, 0));
            handler.Invoke(42, RepairAck(1, RealTimeRepairStage.Verify, 1));
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeRepair>.That.Matches(x => x.Stage == (int)RealTimeRepairStage.Apply && x.State != null)))
                .MustHaveHappenedOnceExactly();
            handler.Invoke(42, RepairAck(1, RealTimeRepairStage.Apply, _multiplayerHost.Game.RealTimeCombat.CanonicalStateHash));

            Assert.That(_multiplayerHost.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(_multiplayerHost.Game.RealTimeCombat.RepairAttempts, Is.Zero);
        }

        [Test]
        public async Task RealTimeRepair_TwoFailedApplications_FallsBackToTurnBasedAfterPeerAck()
        {
            PrepareRealTimeRepairTest();
            var handler = FakeUtils.GetNetworkReceiverHandler<ClientRealTimeRepairAck>(_networkServer);

            await _multiplayerHost.BeginRealTimeRepairAsync();
            for (var epoch = 1; epoch <= 2; epoch++)
            {
                handler.Invoke(42, RepairAck(epoch, RealTimeRepairStage.Prepare, 0));
                handler.Invoke(42, RepairAck(epoch, RealTimeRepairStage.Verify, 1));
                handler.Invoke(42, RepairAck(epoch, RealTimeRepairStage.Apply, 1, applied: false));
            }

            Assert.That(_multiplayerHost.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.FallingBackToTurnBased));
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeRepair>.That.Matches(x => x.Stage == (int)RealTimeRepairStage.Fallback && x.State != null)))
                .MustHaveHappenedOnceExactly();
            handler.Invoke(42, RepairAck(2, RealTimeRepairStage.Fallback, 0, applied: false));
            Assert.That(_multiplayerHost.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.FallingBackToTurnBased));
            Assert.That(_multiplayerHost.IsSavingBlockedByCombatSync, Is.True);
            handler.Invoke(42, RepairAck(2, RealTimeRepairStage.Fallback, _multiplayerHost.Game.RealTimeCombat.CanonicalStateHash));

            Assert.That(_multiplayerHost.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
            Assert.That(_multiplayerHost.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
        }

        [Test]
        public void RealTimeAI_HostPublishesEpochAndSequence()
        {
            A.CallTo(() => _networkServer.IsActive).Returns(true);
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 3 }
            };

            _multiplayerHost.OnAfterAISelectedAction(new NetworkAIAction { UnitId = "enemy-a", Id = "action-a" });
            _multiplayerHost.OnAfterAISelectedAction(new NetworkAIAction { UnitId = "enemy-a", Id = "action-b" });

            A.CallTo(() => _networkServer.Broadcast(A<NotifyAIActionSelected>.That.Matches(x => x.RealTimeEpoch == 3 && x.RealTimeSequence == 1)))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => _networkServer.Broadcast(A<NotifyAIActionSelected>.That.Matches(x => x.RealTimeEpoch == 3 && x.RealTimeSequence == 2)))
                .MustHaveHappenedOnceExactly();
        }

        private void PrepareRealTimeRepairTest()
        {
            A.CallTo(() => _networkServer.IsActive).Returns(true);
            A.CallTo(() => _gameInteractionService.SetPauseAsync(true)).Returns(Task.CompletedTask);
            A.CallTo(() => _gameInteractionService.SetTurnBasedCombatModeAsync(true)).Returns(Task.CompletedTask);
            A.CallTo(() => _combatInteractionService.GetRealTimeCombatState()).Returns(new NetworkCombatState());
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = NetworkConstants.HostPlayerId,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, PreferredMode = CombatMode.RealTimeWithPause }
            };
            _multiplayerHost.Game.Players.Add(new NetworkPlayer(NetworkConstants.HostPlayerId) { LobbySyncStatus = NetworkLobbySyncStatus.Succeed });
            _multiplayerHost.Game.Players.Add(new NetworkPlayer(42) { LobbySyncStatus = NetworkLobbySyncStatus.Succeed });
        }

        [Test]
        public void RealTimePausePolicy_HostPublishesPolicyEvenBeforeModeSwitch()
        {
            A.CallTo(() => _networkServer.IsActive).Returns(true);
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.TurnBased, Epoch = 4 }
            };

            Assert.That(_multiplayerHost.SetRealTimePausePolicy(PausePolicy.AnyoneFreeResume), Is.True);
            Assert.That(_multiplayerHost.Game.RealTimeCombat.PausePolicy, Is.EqualTo(PausePolicy.AnyoneFreeResume));
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimePauseChanged>.That.Matches(x =>
                x.Epoch == 4 && x.Sequence == 1 && x.Policy == (int)PausePolicy.AnyoneFreeResume)))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void PreferredRealTimeMode_IsPublishedAndAutomaticallyPreparedOncePerCombat()
        {
            PrepareRealTimeRepairTest();
            _multiplayerHost.Game.RealTimeCombat.Mode = CombatMode.TurnBased;

            Assert.That(_multiplayerHost.SetPreferredRealTimeCombatMode(CombatMode.RealTimeWithPause), Is.True);
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeCombatPreference>.That.Matches(x =>
                x.Sequence == 1 && x.PreferredMode == (int)CombatMode.RealTimeWithPause)))
                .MustHaveHappenedOnceExactly();

            _multiplayerHost.PublishRealTimeCombatSnapshot();
            _multiplayerHost.PublishRealTimeCombatSnapshot();

            Assert.That(_multiplayerHost.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.PreparingModeSwitch));
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeModeTransition>.That.Matches(x =>
                x.Stage == (int)RealTimeModeTransitionStage.Prepare))).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task RealTimeModeBarrier_RetransmitsBeforeSafeAbort()
        {
            PrepareRealTimeRepairTest();
            _multiplayerHost.Game.RealTimeCombat.Mode = CombatMode.TurnBased;
            Assert.That(await _multiplayerHost.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            var deadline = typeof(MultiplayerHost).GetField("_realTimeModeSwitchDeadline",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            deadline.SetValue(_multiplayerHost, DateTime.UtcNow.AddSeconds(-1));
            _multiplayerHost.PublishRealTimeCombatSnapshot();

            Assert.That(_multiplayerHost.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.PreparingModeSwitch));
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeModeTransition>.That.Matches(x =>
                x.Stage == (int)RealTimeModeTransitionStage.Prepare))).MustHaveHappenedTwiceExactly();
        }

        [Test]
        public async Task RealTimeRepairBarrier_RetransmitsBeforeCountingFailure()
        {
            PrepareRealTimeRepairTest();
            await _multiplayerHost.BeginRealTimeRepairAsync();
            var deadline = typeof(MultiplayerHost).GetField("_realTimeRepairDeadline",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            deadline.SetValue(_multiplayerHost, DateTime.UtcNow.AddSeconds(-1));
            _multiplayerHost.PublishRealTimeCombatSnapshot();

            Assert.That(_multiplayerHost.Game.RealTimeCombat.RepairAttempts, Is.EqualTo(1));
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeRepair>.That.Matches(x =>
                x.Stage == (int)RealTimeRepairStage.Prepare))).MustHaveHappenedTwiceExactly();
        }

        private static ClientRealTimeRepairAck RepairAck(long epoch, RealTimeRepairStage stage, ulong hash, bool applied = true)
        {
            return new ClientRealTimeRepairAck
            {
                Epoch = epoch,
                Stage = (int)stage,
                PlayerId = 42,
                CanonicalStateHash = hash,
                Applied = applied
            };
        }

        [Test]
        public void LocalHostRealTimeCommand_IsBroadcastWithoutExecutingTwice()
        {
            A.CallTo(() => _networkServer.IsActive).Returns(true);
            var hostPlayer = new NetworkPlayer(NetworkConstants.HostPlayerId);
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = NetworkConstants.HostPlayerId,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 3 }
            };
            _multiplayerHost.Game.Players.Add(hostPlayer);
            _multiplayerHost.Game.Characters.Add(new NetworkCharacter { UnitId = "host-unit", Owner = hostPlayer });

            var accepted = _multiplayerHost.PublishLocalRealTimeCombatCommand(RealTimeCommandKind.Move,
                movement: new NetworkUnitMoveTo
                {
                    InitiatorUnitId = "host-unit",
                    Destination = new NetworkVector3 { X = 4, Y = 5, Z = 6 }
                });

            Assert.That(accepted, Is.True);
            A.CallTo(() => _combatInteractionService.MoveUnit(A<NetworkUnitMoveTo>._)).MustNotHaveHappened();
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeCombatCommandAccepted>.That.Matches(x => x.PlayerId == NetworkConstants.HostPlayerId && x.AuthoritativeSequence == 1)))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void RealTimeBarrier_BlocksHostOrderBroadcast()
        {
            A.CallTo(() => _networkServer.IsActive).Returns(true);
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = NetworkConstants.HostPlayerId,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState
                {
                    Mode = CombatMode.RealTimeWithPause,
                    Phase = CombatSynchronizationPhase.PreparingModeSwitch,
                    Epoch = 3
                }
            };

            Assert.That(_multiplayerHost.IsRealTimeCombatBarrierActive, Is.True);
            Assert.That(_multiplayerHost.PublishLocalRealTimeCombatCommand(RealTimeCommandKind.Move,
                movement: new NetworkUnitMoveTo { InitiatorUnitId = "host-unit", Destination = new NetworkVector3() }), Is.False);
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimeCombatCommandAccepted>._)).MustNotHaveHappened();
        }

        [Test]
        public void RealTimePause_ConsensusRequiresBothPlayers()
        {
            A.CallTo(() => _networkServer.IsActive).Returns(true);
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = NetworkConstants.HostPlayerId,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState
                {
                    Mode = CombatMode.RealTimeWithPause,
                    Epoch = 7,
                    PausePolicy = PausePolicy.AnyoneConsensusResume
                }
            };
            _multiplayerHost.Game.Players.Add(new NetworkPlayer(NetworkConstants.HostPlayerId) { LobbySyncStatus = NetworkLobbySyncStatus.Succeed });
            _multiplayerHost.Game.Players.Add(new NetworkPlayer(42) { LobbySyncStatus = NetworkLobbySyncStatus.Succeed });
            var handler = FakeUtils.GetNetworkReceiverHandler<ClientRealTimePauseRequested>(_networkServer);

            handler.Invoke(42, new ClientRealTimePauseRequested { Epoch = 7, RequestSequence = 1, PlayerId = 42, WantsPause = true });
            handler.Invoke(42, new ClientRealTimePauseRequested { Epoch = 7, RequestSequence = 2, PlayerId = 42, WantsPause = false });
            Assert.That(_multiplayerHost.Game.RealTimeCombat.IsManualPauseActive, Is.True);

            Assert.That(_multiplayerHost.RequestLocalRealTimePause(wantsPause: false), Is.True);
            Assert.That(_multiplayerHost.Game.RealTimeCombat.IsManualPauseActive, Is.False);
            A.CallTo(() => _gameInteractionService.SetPause(true)).MustHaveHappenedTwiceExactly();
            A.CallTo(() => _gameInteractionService.SetPause(false)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _networkServer.Broadcast(A<NotifyRealTimePauseChanged>.That.Matches(x => x.Epoch == 7 && x.Sequence == 3 && !x.IsPaused)))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void OnNotifyDropItem__CallsGameInteraction()
        {
            // Arrange
            var startUp = new NetworkGameStartUp() { SavePath = Guid.NewGuid().ToString(), IsNewGameSequence = false };
            var gameId = Guid.NewGuid().ToString();
            var settings = new NetworkMultiplayerSettings() { HostPortRangeStart = 123, HostPortRangeEnd = 1234 };
            A.CallTo(() => _multiplayerSettingsProvider.GetSettings()).Returns(settings);
            _multiplayerHost.Create(gameId, null, null, startUp);
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyDropItem>(_networkServer);
            var request = new NotifyDropItem { Drop = new Networking.Messages.Contracts.NetworkDropItem { Item = new Networking.Messages.Contracts.NetworkItem() } };
            var playerId = 123;

            // Act
            handler.Invoke(playerId, request);

            // Assert
            A.CallTo(() => _gameInteractionService.DropItem(A<NetworkDropItem>.Ignored)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void OnNotifyRestEnded__AddsToReadyListAndCallsGameInteractionService()
        {
            // Arrange
            var startUp = new NetworkGameStartUp() { SavePath = Guid.NewGuid().ToString(), IsNewGameSequence = false };
            var gameId = Guid.NewGuid().ToString();
            var settings = new NetworkMultiplayerSettings() { HostPortRangeStart = 123, HostPortRangeEnd = 1234 };
            A.CallTo(() => _multiplayerSettingsProvider.GetSettings()).Returns(settings);
            _multiplayerHost.Create(gameId, null, null, startUp);
            _multiplayerHost.Game.Rest = new NetworkRest();
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRestEnded>(_networkServer);
            var playerId = 123;
            var message = new NotifyRestEnded { PlayerId = playerId };

            // Act
            handler.Invoke(playerId, message);

            // Assert
            A.CallTo(() => _gameInteractionService.UpdateRestUI(true, 1, 0)).MustHaveHappenedOnceExactly();
            Assert.That(_multiplayerHost.Game.Rest.PlayersFinishedRest, Contains.Item(playerId));
        }

        [Test]
        public void ClientGameServerConnectionConfirmed_PlayerNameExists_PlayerNameIsUpdated()
        {
            // Arrange
            var startUp = new NetworkGameStartUp() { SavePath = Guid.NewGuid().ToString(), IsNewGameSequence = false };
            var gameId = Guid.NewGuid().ToString();
            var settings = new NetworkMultiplayerSettings() { HostPortRangeStart = 123, HostPortRangeEnd = 1234 };
            A.CallTo(() => _multiplayerSettingsProvider.GetSettings()).Returns(settings);
            _multiplayerHost.Create(gameId, null, null, startUp);
            var hostPlayer = new NetworkPlayer { Id = NetworkConstants.HostPlayerId, IsHost = true };
            var player = new NetworkPlayer { Id = 123 };
            _multiplayerHost.Game.Players.AddRange([hostPlayer, player]);
            var handler = FakeUtils.GetNetworkReceiverHandler<ClientGameServerConnectionConfirmed>(_networkServer);
            var message = new ClientGameServerConnectionConfirmed
            {
                PlayerName = Guid.NewGuid().ToString(),
                ContentState = new Networking.Messages.Contracts.NetworkContentState()
            };

            // Act
            handler.Invoke(player.Id, message);

            // Assert
            Assert.That(player.Name, Is.EqualTo(message.PlayerName));
        }

        [Test]
        public void ClientGameServerConnectionConfirmed_BothHaveEmptyContentState_PlayerStateRemainsUnchanged()
        {
            // Arrange
            var startUp = new NetworkGameStartUp() { SavePath = Guid.NewGuid().ToString(), IsNewGameSequence = false };
            var gameId = Guid.NewGuid().ToString();
            var settings = new NetworkMultiplayerSettings() { HostPortRangeStart = 123, HostPortRangeEnd = 1234 };
            A.CallTo(() => _multiplayerSettingsProvider.GetSettings()).Returns(settings);
            _multiplayerHost.Create(gameId, null, null, startUp);
            var hostPlayer = new NetworkPlayer { Id = NetworkConstants.HostPlayerId, IsHost = true };
            var player = new NetworkPlayer { Id = 123 };
            _multiplayerHost.Game.Players.AddRange([hostPlayer, player]);
            var handler = FakeUtils.GetNetworkReceiverHandler<ClientGameServerConnectionConfirmed>(_networkServer);
            var message = new ClientGameServerConnectionConfirmed
            {
                PlayerName = Guid.NewGuid().ToString(),
                ContentState = new Networking.Messages.Contracts.NetworkContentState()
            };

            // Act
            handler.Invoke(player.Id, message);

            // Assert
            Assert.That(player.ContentState.DiscrepantDLCs, Has.Count.EqualTo(0));
            Assert.That(player.ContentState.DiscrepantMods, Has.Count.EqualTo(0));
        }

        [TestCaseSource(nameof(DlcDifferencesTestCases))]
        public void ClientGameServerConnectionConfirmed_DlcContentStateIsDifferent_DiscrepantListContainsCorrectReasonAndNumberOfDLCs(ContentStateTestCase testCase)
        {
            // Arrange
            var startUp = new NetworkGameStartUp() { SavePath = Guid.NewGuid().ToString(), IsNewGameSequence = false };
            var gameId = Guid.NewGuid().ToString();
            var settings = new NetworkMultiplayerSettings() { HostPortRangeStart = 123, HostPortRangeEnd = 1234 };
            A.CallTo(() => _multiplayerSettingsProvider.GetSettings()).Returns(settings);
            _multiplayerHost.Create(gameId, null, null, startUp);
            var hostPlayer = new NetworkPlayer
            {
                Id = NetworkConstants.HostPlayerId,
                IsHost = true,
                ContentState = new NetworkContentState
                {
                    DLCs = testCase.HostDLCs,
                }
            };
            var player = new NetworkPlayer { Id = 123 };
            _multiplayerHost.Game.Players.AddRange([hostPlayer, player]);
            var handler = FakeUtils.GetNetworkReceiverHandler<ClientGameServerConnectionConfirmed>(_networkServer);
            var message = new ClientGameServerConnectionConfirmed
            {
                PlayerName = Guid.NewGuid().ToString(),
                ContentState = new Networking.Messages.Contracts.NetworkContentState
                {
                    DLCs = testCase.PlayerDLCs
                },
            };

            // Act
            handler.Invoke(player.Id, message);

            // Assert
            Assert.That(player.ContentState.DiscrepantDLCs, Has.Count.EqualTo(testCase.ExpectedDiscrepantDLCs.Count));
            foreach (var expectedDiscrepantDLC in testCase.ExpectedDiscrepantDLCs)
            {
                var actualDiscrepantDLC = player.ContentState.DiscrepantDLCs.FirstOrDefault(p => p.DLC.Id == expectedDiscrepantDLC.DLC.Id);
                Assert.That(actualDiscrepantDLC, Is.Not.Null);
                Assert.That(actualDiscrepantDLC.Reason, Is.EqualTo(expectedDiscrepantDLC.Reason));
            }
        }

        [TestCaseSource(nameof(ModDifferencesTestCases))]
        public void ClientGameServerConnectionConfirmed_ModContentStateIsDifferent_DiscrepantListContainsCorrectReasonAndNumberOfDLCs(ContentStateTestCase testCase)
        {
            // Arrange
            var startUp = new NetworkGameStartUp() { SavePath = Guid.NewGuid().ToString(), IsNewGameSequence = false };
            var gameId = Guid.NewGuid().ToString();
            var settings = new NetworkMultiplayerSettings() { HostPortRangeStart = 123, HostPortRangeEnd = 1234 };
            A.CallTo(() => _multiplayerSettingsProvider.GetSettings()).Returns(settings);
            _multiplayerHost.Create(gameId, null, null, startUp);
            var hostPlayer = new NetworkPlayer
            {
                Id = NetworkConstants.HostPlayerId,
                IsHost = true,
                ContentState = new NetworkContentState
                {
                    Mods = testCase.HostMods,
                }
            };
            var player = new NetworkPlayer { Id = 123 };
            _multiplayerHost.Game.Players.AddRange([hostPlayer, player]);
            var handler = FakeUtils.GetNetworkReceiverHandler<ClientGameServerConnectionConfirmed>(_networkServer);
            var message = new ClientGameServerConnectionConfirmed
            {
                PlayerName = Guid.NewGuid().ToString(),
                ContentState = new Networking.Messages.Contracts.NetworkContentState
                {
                    Mods = testCase.PlayerMods
                },
            };

            // Act
            handler.Invoke(player.Id, message);

            // Assert
            Assert.That(player.ContentState.DiscrepantMods, Has.Count.EqualTo(testCase.ExpectedDiscrepantMods.Count));
            foreach (var expectedDiscrepantMod in testCase.ExpectedDiscrepantMods)
            {
                var actualDiscrepantMod = player.ContentState.DiscrepantMods.FirstOrDefault(p => p.Id == expectedDiscrepantMod.Id);
                Assert.That(actualDiscrepantMod, Is.Not.Null);
                Assert.That(actualDiscrepantMod.Reason, Is.EqualTo(expectedDiscrepantMod.Reason));
            }
        }

        [TestCase(GameModeType.Enum.None)]
        [TestCase(GameModeType.Enum.Default)]
        [TestCase(GameModeType.Enum.Cutscene)]
        [TestCase(GameModeType.Enum.CutsceneGlobalMap)]
        [TestCase(GameModeType.Enum.GameOver)]
        [TestCase(GameModeType.Enum.Dialog)]
        [TestCase(GameModeType.Enum.Rest)]
        public void OnStartGameMode_ModeIsRegistered(GameModeType.Enum mode)
        {
            // Arrange
            var gameMode = GameModeType.All.First(x => x.Index == (int)mode);
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 12312313
            };

            // Act
            _multiplayerHost.OnStartGameMode(gameMode);

            // Assert
            _multiplayerHost.Game.PlayersInGameMode.TryGetValue(gameMode, out var players);
            Assert.That(players, Is.Not.Empty);
            Assert.That(players, Does.Contain(_multiplayerHost.Game.LocalPlayerId));
        }

        [Test]
        public void OnStartGameMode_RestMode_RestUIIsUpdated()
        {
            // Arrange
            var gameMode = GameModeType.All.First(x => x.Index == (int)GameModeType.Enum.Rest);
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = NetworkConstants.HostPlayerId,
                Players = [
                    new NetworkPlayer { Id = NetworkConstants.HostPlayerId, LobbySyncStatus = NetworkLobbySyncStatus.Succeed },
                    new NetworkPlayer { Id = 123123, LobbySyncStatus = NetworkLobbySyncStatus.Succeed },
                    ]
            };

            // Act
            _multiplayerHost.OnStartGameMode(gameMode);

            // Assert
            _multiplayerHost.Game.PlayersInGameMode.TryGetValue(gameMode, out var players);
            A.CallTo(() => _gameInteractionService.UpdateRestUI(false, players.Count, _multiplayerHost.Game.Players.Count)).MustHaveHappened();
        }

        [TestCase(SeedKind.Session | SeedKind.LoadedSaveSeed, "Session=1,LoadedSaveSeed=2,AreaSeed=excluded,CombatSeed=excluded,CombatTurnSeed=excluded,CrusadeArmyCombatAreaSeed=excluded,CrusadeArmyCombatSeed=excluded")]
        [TestCase(SeedKind.Session, "Session=1,LoadedSaveSeed=excluded,AreaSeed=excluded,CombatSeed=excluded,CombatTurnSeed=excluded,CrusadeArmyCombatAreaSeed=excluded,CrusadeArmyCombatSeed=excluded")]
        [TestCase(SeedKind.All, "Session=1,LoadedSaveSeed=2,AreaSeed=3,CombatSeed=4,CombatTurnSeed=0,CrusadeArmyCombatAreaSeed=5,CrusadeArmyCombatSeed=6")]
        [TestCase(SeedKind.All & ~SeedKind.AreaSeed, "Session=1,LoadedSaveSeed=2,AreaSeed=excluded,CombatSeed=4,CombatTurnSeed=0,CrusadeArmyCombatAreaSeed=5,CrusadeArmyCombatSeed=6")]
        public void GetSeededContext_CombatTurnIsNull_ReturnsCorrectIdAndLifetime(SeedKind seedKind, string expected)
        {
            // Arrange
            var gameMode = GameModeType.All.First(x => x.Index == (int)GameModeType.Enum.Rest);
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = NetworkConstants.HostPlayerId,
                Players = [new NetworkPlayer { Id = NetworkConstants.HostPlayerId, LobbySyncStatus = NetworkLobbySyncStatus.Succeed }],
                SessionSeed = 1,
                LoadedSaveSeed = 2,
                CurrentArea = new Entities.Area.NetworkArea { Seed = 3 },
                Combat = new NetworkCombat { Seed = 4 },
                ArmyCombat = new NetworkArmyCombat { Seed = 6, AreaSeed = 5 },
            };

            // Act
            var context = _multiplayerHost.GetSeededContext(seedKind);

            // Assert
            Assert.That(context, Is.Not.Null);
            Assert.That(context.Id, Is.EqualTo(expected));
            Assert.That(context.Lifetime, Is.EqualTo(IdentifierLifetime.Area));
        }

        [TestCase(SeedKind.Session, "Session=1,LoadedSaveSeed=excluded,AreaSeed=excluded,CombatSeed=excluded,CombatTurnSeed=excluded,CrusadeArmyCombatAreaSeed=excluded,CrusadeArmyCombatSeed=excluded")]
        [TestCase(SeedKind.All, "Session=1,LoadedSaveSeed=2,AreaSeed=3,CombatSeed=4,CombatTurnSeed=99,CrusadeArmyCombatAreaSeed=5,CrusadeArmyCombatSeed=6")]
        public void GetSeededContext_CombatTurnIsSet_ReturnsCorrectIdAndLifetime(SeedKind seedKind, string expected)
        {
            // Arrange
            var gameMode = GameModeType.All.First(x => x.Index == (int)GameModeType.Enum.Rest);
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = NetworkConstants.HostPlayerId,
                Players = [new NetworkPlayer { Id = NetworkConstants.HostPlayerId, LobbySyncStatus = NetworkLobbySyncStatus.Succeed }],
                SessionSeed = 1,
                LoadedSaveSeed = 2,
                CurrentArea = new Entities.Area.NetworkArea { Seed = 3 },
                Combat = new NetworkCombat { Seed = 4, Turn = new NetworkCombatTurn { Seed = 99 } },
                ArmyCombat = new NetworkArmyCombat { Seed = 6, AreaSeed = 5 },
            };

            // Act
            var context = _multiplayerHost.GetSeededContext(seedKind);

            // Assert
            Assert.That(context, Is.Not.Null);
            Assert.That(context.Id, Is.EqualTo(expected));
            Assert.That(context.Lifetime, Is.EqualTo(IdentifierLifetime.CombatTurn));
        }

        private static IEnumerable<ContentStateTestCase> DlcDifferencesTestCases()
        {
            const string dlcId1 = "dlc1";
            yield return new ContentStateTestCase("dlc is available on host, but unavailable at player")
            {
                HostDLCs = [new NetworkDLC { Id = dlcId1, IsAvailable = true }],
                PlayerDLCs = [new Networking.Messages.Contracts.NetworkDLC { Id = dlcId1, IsAvailable = false }],
                ExpectedDiscrepantDLCs = [new NetworkDiscrepantDLC { DLC = new NetworkDLC { Id = dlcId1 }, Reason = NetworkDiscrepancyReason.Missing }]
            };

            yield return new ContentStateTestCase("dlc is unavailable on host, but available at player")
            {
                HostDLCs = [new NetworkDLC { Id = dlcId1, IsAvailable = false }],
                PlayerDLCs = [new Networking.Messages.Contracts.NetworkDLC { Id = dlcId1, IsAvailable = true }],
                ExpectedDiscrepantDLCs = [new NetworkDiscrepantDLC { DLC = new NetworkDLC { Id = dlcId1 }, Reason = NetworkDiscrepancyReason.Extra }]
            };

            yield return new ContentStateTestCase("dlc is unavailable for both")
            {
                HostDLCs = [new NetworkDLC { Id = dlcId1, IsAvailable = false }],
                PlayerDLCs = [new Networking.Messages.Contracts.NetworkDLC { Id = dlcId1, IsAvailable = false }],
                ExpectedDiscrepantDLCs = []
            };

            yield return new ContentStateTestCase("dlc is available for both")
            {
                HostDLCs = [new NetworkDLC { Id = dlcId1, IsAvailable = true }],
                PlayerDLCs = [new Networking.Messages.Contracts.NetworkDLC { Id = dlcId1, IsAvailable = true }],
                ExpectedDiscrepantDLCs = []
            };
        }

        private static IEnumerable<ContentStateTestCase> ModDifferencesTestCases()
        {
            const string modId1 = "mod1";
            yield return new ContentStateTestCase("host-only voice mods are local and do not block multiplayer")
            {
                HostMods = [new NetworkMod { Id = "SpeechMod", IsEnabled = true, Version = "1.3.0" }, new NetworkMod { Id = "WOTR_AIVO", IsEnabled = true, Version = "0.2.0" }],
                PlayerMods = [],
                ExpectedDiscrepantMods = []
            };

            yield return new ContentStateTestCase("client-only voice mods are local and do not block multiplayer")
            {
                HostMods = [],
                PlayerMods = [new Networking.Messages.Contracts.NetworkMod { Id = "speechmod", IsEnabled = true }],
                ExpectedDiscrepantMods = []
            };

            yield return new ContentStateTestCase("mod is enabled on host, but disabled at player")
            {
                HostMods = [new NetworkMod { Id = modId1, IsEnabled = true }],
                PlayerMods = [new Networking.Messages.Contracts.NetworkMod { Id = modId1, IsEnabled = false }],
                ExpectedDiscrepantMods = [new NetworkDiscrepantMod(modId1, NetworkModType.UnityModManager, null, null, NetworkDiscrepancyReason.Disabled)]
            };

            yield return new ContentStateTestCase("mod is disabled on host, but enabled at player")
            {
                HostMods = [new NetworkMod { Id = modId1, IsEnabled = false }],
                PlayerMods = [new Networking.Messages.Contracts.NetworkMod { Id = modId1, IsEnabled = true }],
                ExpectedDiscrepantMods = [new NetworkDiscrepantMod(modId1, NetworkModType.UnityModManager, null, null, NetworkDiscrepancyReason.Extra)]
            };

            yield return new ContentStateTestCase("mod is installed and enabled on host, but it's not installed at player")
            {
                HostMods = [new NetworkMod { Id = modId1, IsEnabled = true }],
                PlayerMods = [],
                ExpectedDiscrepantMods = [new NetworkDiscrepantMod(modId1, NetworkModType.UnityModManager, null, null, NetworkDiscrepancyReason.Missing)]
            };

            yield return new ContentStateTestCase("mod is disabled for both")
            {
                HostMods = [new NetworkMod { Id = modId1, IsEnabled = false }],
                PlayerMods = [new Networking.Messages.Contracts.NetworkMod { Id = modId1, IsEnabled = false }],
                ExpectedDiscrepantMods = []
            };

            yield return new ContentStateTestCase("mod is not installed on host, but it's installed and disabled at player")
            {
                HostMods = [],
                PlayerMods = [new Networking.Messages.Contracts.NetworkMod { Id = modId1, IsEnabled = false }],
                ExpectedDiscrepantMods = []
            };

            yield return new ContentStateTestCase("mod is installed and disabled on host, but it's not installed at player")
            {
                HostMods = [new NetworkMod { Id = modId1, IsEnabled = false }],
                PlayerMods = [],
                ExpectedDiscrepantMods = []
            };

            yield return new ContentStateTestCase("mod is installed and enabled for both")
            {
                HostMods = [new NetworkMod { Id = modId1, IsEnabled = true }],
                PlayerMods = [new Networking.Messages.Contracts.NetworkMod { Id = modId1, IsEnabled = true }],
                ExpectedDiscrepantMods = []
            };

            yield return new ContentStateTestCase("different mod version")
            {
                HostMods = [new NetworkMod { Id = modId1, IsEnabled = true, Version = "1111" }],
                PlayerMods = [new Networking.Messages.Contracts.NetworkMod { Id = modId1, IsEnabled = true, Version = "2222" }],
                ExpectedDiscrepantMods = [new NetworkDiscrepantMod(modId1, NetworkModType.UnityModManager, "2222", "1111", NetworkDiscrepancyReason.VersionMismatch)]
            };
        }

        private bool IsSameConfiguration(NetworkServerConfiguration networkServerConfiguration, NetworkMultiplayerSettings settings)
        {
            var isSame = networkServerConfiguration.Host == settings.Host
                && networkServerConfiguration.PortRangeStart == settings.HostPortRangeStart
                && networkServerConfiguration.PortRangeEnd == settings.HostPortRangeEnd
                && networkServerConfiguration.UseIPv6 == settings.UseIPv6
                && networkServerConfiguration.AwaiterTimeout == settings.NetworkAwaiterTimeout;

            return isSame;
        }
    }
}
