using System;
using AutoMapper;
using System.Threading.Tasks;
using System.Threading;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Abstractions.IO;
using WOTRMultiplayer.Abstractions.Random;
using WOTRMultiplayer.Abstractions.Settings;
using WOTRMultiplayer.Config.Mapping;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Abstractions;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.UnitTests.FakeRules;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class MultiplayerClientTests
    {
        private MultiplayerClient _multiplayerClient;

        private ILogger<MultiplayerClient> _logger;
        private IGameInteractionService _gameInteractionService;
        private ILevelingInteractionService _levelingInteractionService;
        private IPlayerNotificationService _playerNotificationService;
        private IDialogInteractionService _dialogInteractionService;
        private IGlobalMapInteractionService _globalMapInteractionService;
        private IPingInteractionService _pingInteractionService;
        private ICombatInteractionService _combatInteractionService;
        private IMultiplayerSettingsService _multiplayerSettingsProvider;
        private IFileSystemService _fileSystemService;
        private INetworkClientConnection _networkClient;
        private IValueGenerator _valueGenerator;
        private IMapper _mapper;

        [Test]
        public void RealTimeSynchronizationBarrier_BlocksSaving()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                RealTimeCombat = new NetworkRealTimeCombatState { Phase = CombatSynchronizationPhase.PreparingModeSwitch }
            };

            Assert.That(_multiplayerClient.IsSavingBlockedByCombatSync, Is.True);
            _multiplayerClient.Game.RealTimeCombat.Phase = CombatSynchronizationPhase.Stable;
            Assert.That(_multiplayerClient.IsSavingBlockedByCombatSync, Is.False);
        }

        [Test]
        public void RunningRealTimeCombat_BlocksSavingUntilTurnBasedOrCombatEnd()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause }
            };

            Assert.That(_multiplayerClient.IsSavingBlockedByCombatSync, Is.True);
            _multiplayerClient.Game.Combat = null;
            Assert.That(_multiplayerClient.IsSavingBlockedByCombatSync, Is.False);
        }

        [Test]
        public void DisconnectDuringRealTimeCombat_BlocksSavingUntilSessionReset()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause }
            };

            _networkClient.OnError(new NetworkError(NetworkErrorType.Disconnected));
            _multiplayerClient.Game.Combat = null;
            Assert.That(_multiplayerClient.IsSavingBlockedByCombatSync, Is.True);
            Assert.That(_multiplayerClient.IsSavingBlockedAfterRealTimeDisconnect, Is.True);

            _multiplayerClient.Reset();
            Assert.That(_multiplayerClient.IsSavingBlockedByCombatSync, Is.False);
            Assert.That(_multiplayerClient.IsSavingBlockedAfterRealTimeDisconnect, Is.False);
        }

        [Test]
        public void VerifiedTurnBasedRepair_ClearsDisconnectSaveLatch()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState
                {
                    Mode = CombatMode.RealTimeWithPause,
                    Phase = CombatSynchronizationPhase.Repairing,
                    Epoch = 8
                }
            };
            _networkClient.OnError(new NetworkError(NetworkErrorType.Disconnected));
            Assert.That(_multiplayerClient.IsSavingBlockedAfterRealTimeDisconnect, Is.True);
            _multiplayerClient.Game.RealTimeCombat.Phase = CombatSynchronizationPhase.FallingBackToTurnBased;

            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRepair>(_networkClient)(
                NetworkConstants.HostPlayerId, new NotifyRealTimeRepair
                {
                    Epoch = 8,
                    Stage = (int)RealTimeRepairStage.Resume,
                    Mode = (int)CombatMode.TurnBased,
                    CanonicalStateHash = 47
                });

            Assert.That(_multiplayerClient.IsSavingBlockedAfterRealTimeDisconnect, Is.False);
            Assert.That(_multiplayerClient.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
            Assert.That(_multiplayerClient.IsSavingBlockedByCombatSync, Is.False);
        }

        [Test]
        public void RealTimeDiceRoll_UsesHostResultAndStillAdvancesLocalGenerator()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState
                {
                    Mode = CombatMode.RealTimeWithPause, Epoch = 7
                }
            };
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRuleRoll>(_networkClient).Invoke(-1,
                new NotifyRealTimeRuleRoll { Epoch = 7, Key = "attack:d20", Sequence = 1, Result = 19 });
            var localRolls = 0;

            var result = _multiplayerClient.ResolveRealTimeDiceRoll("attack:d20", () =>
            {
                localRolls++;
                return 12;
            });

            Assert.That(result, Is.EqualTo(19));
            Assert.That(localRolls, Is.EqualTo(1));
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeRuleRollFault>.That.Matches(
                fault => fault.Epoch == 7 && fault.Sequence == 1
                    && fault.Reason == "local-result-mismatch"))).MustHaveHappenedOnceExactly();
        }

        [SetUp]
        public void SetUp()
        {
            _mapper = new MapperConfiguration(x =>
            {
                x.AddProfile<NetworkMessagesProfile>();
            }).CreateMapper();

            _logger = A.Fake<ILogger<MultiplayerClient>>();
            _gameInteractionService = A.Fake<IGameInteractionService>();
            _levelingInteractionService = A.Fake<ILevelingInteractionService>();
            _playerNotificationService = A.Fake<IPlayerNotificationService>();
            _dialogInteractionService = A.Fake<IDialogInteractionService>();
            _globalMapInteractionService = A.Fake<IGlobalMapInteractionService>();
            _pingInteractionService = A.Fake<IPingInteractionService>();
            _combatInteractionService = A.Fake<ICombatInteractionService>();
            _multiplayerSettingsProvider = A.Fake<IMultiplayerSettingsService>();
            _fileSystemService = A.Fake<IFileSystemService>();

            _networkClient = A.Fake<INetworkClientConnection>();
            Fake.GetFakeManager(_networkClient).AddRuleFirst(new NetworkReceiverFakeRule<INetworkConnection>());

            _valueGenerator = A.Fake<IValueGenerator>();

            _multiplayerClient = new MultiplayerClient(
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
                _networkClient,
                _valueGenerator,
                _mapper);
        }

        [Test]
        public void Connect_ValidAddress_CallsConnectOnNetworkClient()
        {
            // Arrange
            var host = "192.168.1.1";
            var port = 555;

            // Act
            _multiplayerClient.Connect(host, port);

            // Assert
            A.CallTo(() => _networkClient.ConnectAsync(host, port, A<TimeSpan>.Ignored)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void RealTimeSnapshot_CurrentSequence_IsAppliedAndAcknowledged()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 2 }
            };
            A.CallTo(() => _combatInteractionService.UpdateRealTimeCombatStateAsync(A<NetworkCombatState>._))
                .Returns(Task.CompletedTask);
            A.CallTo(() => _combatInteractionService.GetRealTimeCombatStateAsync()).Returns(Task.FromResult(new NetworkCombatState()));
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(_networkClient);

            handler.Invoke(-1, new NotifyRealTimeCombatSnapshot
            {
                Epoch = 2,
                Sequence = 1,
                ResponseTimeMs = 42,
                MissingReplies = 3,
                State = new WOTRMultiplayer.Networking.Messages.Contracts.NetworkCombatState()
            });

            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeStateHash>.That.Matches(x => x.Epoch == 2 && x.Sequence == 1)))
                .MustHaveHappenedOnceExactly();
            Assert.That(_multiplayerClient.Game.RealTimeCombat.LastAuthoritativeSequence, Is.EqualTo(1));
            Assert.That(_multiplayerClient.Game.RealTimeCombat.ResponseTimeMs, Is.EqualTo(42));
            Assert.That(_multiplayerClient.Game.RealTimeCombat.MissingSnapshotReplies, Is.EqualTo(3));
        }

        [Test]
        public void RealTimeSnapshot_FailedApplication_ReportsFailureToHost()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 2 }
            };
            A.CallTo(() => _combatInteractionService.UpdateRealTimeCombatStateAsync(A<NetworkCombatState>._))
                .Returns(Task.FromException(new InvalidOperationException("missing unit")));
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(_networkClient);

            handler.Invoke(-1, new NotifyRealTimeCombatSnapshot
            {
                Epoch = 2, Sequence = 1,
                State = new WOTRMultiplayer.Networking.Messages.Contracts.NetworkCombatState()
            });

            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeStateHash>.That.Matches(x =>
                x.Epoch == 2 && x.Sequence == 1 && !x.Applied && x.FailureReason.Contains("missing unit"))))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void RealTimeCommand_OnlyOwnedUnitIsSentWithIncreasingSequence()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 5 }
            };
            _multiplayerClient.Game.Characters.Add(new NetworkCharacter
            {
                UnitId = "unit-a",
                Owner = new NetworkPlayer(42)
            });
            var ownMove = new NetworkUnitMoveTo
            {
                InitiatorUnitId = "unit-a",
                Destination = new NetworkVector3 { X = 1, Y = 2, Z = 3 }
            };
            var otherMove = new NetworkUnitMoveTo
            {
                InitiatorUnitId = "unit-b",
                Destination = new NetworkVector3 { X = 1, Y = 2, Z = 3 }
            };

            Assert.That(_multiplayerClient.SubmitRealTimeCombatCommand(RealTimeCommandKind.Move, movement: otherMove), Is.False);
            Assert.That(_multiplayerClient.SubmitRealTimeCombatCommand(RealTimeCommandKind.Move, movement: ownMove), Is.True);
            Assert.That(_multiplayerClient.SubmitRealTimeCombatCommand(RealTimeCommandKind.Move, movement: ownMove), Is.True);

            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeCombatCommand>.That.Matches(x => x.PlayerId == 42 && x.Epoch == 5 && x.Sequence == 1)))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeCombatCommand>.That.Matches(x => x.PlayerId == 42 && x.Epoch == 5 && x.Sequence == 2)))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void RealTimeCommand_IsNotSentDuringSynchronizationBarrier()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState
                {
                    Mode = CombatMode.RealTimeWithPause,
                    Phase = CombatSynchronizationPhase.PreparingModeSwitch,
                    Epoch = 6
                }
            };
            _multiplayerClient.Game.Characters.Add(new NetworkCharacter { UnitId = "unit-a", Owner = new NetworkPlayer(42) });

            Assert.That(_multiplayerClient.IsRealTimeCombatActive, Is.True);
            Assert.That(_multiplayerClient.SubmitRealTimeCombatCommand(RealTimeCommandKind.Move,
                movement: new NetworkUnitMoveTo { InitiatorUnitId = "unit-a", Destination = new NetworkVector3() }), Is.False);
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeCombatCommand>._)).MustNotHaveHappened();
        }

        [Test]
        public void AcceptedRealTimeCommand_IsAppliedOnceAndStaleRepeatIsIgnored()
        {
            A.CallTo(() => _combatInteractionService.ExecuteRealTimeCombatCommandAsync(RealTimeCommandKind.Move,
                A<NetworkUnitMoveTo>._, null, null)).Returns(Task.FromResult(true));
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 5 }
            };
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatCommandAccepted>(_networkClient);
            var accepted = new NotifyRealTimeCombatCommandAccepted
            {
                Epoch = 5,
                AuthoritativeSequence = 1,
                Kind = (int)RealTimeCommandKind.Move,
                Movement = new Networking.Messages.Contracts.NetworkUnitMoveTo
                {
                    InitiatorUnitId = "unit-a",
                    Destination = new Networking.Messages.Contracts.NetworkVector3(1, 2, 3)
                }
            };

            handler.Invoke(-1, accepted);
            handler.Invoke(-1, accepted);

            A.CallTo(() => _combatInteractionService.ExecuteRealTimeCombatCommandAsync(RealTimeCommandKind.Move,
                A<NetworkUnitMoveTo>.That.Matches(x => x.InitiatorUnitId == "unit-a"), null, null))
                .MustHaveHappenedOnceExactly();
            Assert.That(_multiplayerClient.Game.RealTimeCombat.LastAuthoritativeSequence, Is.EqualTo(1));
        }

        [Test]
        public void FailedRealTimeCommand_DoesNotAdvanceAuthoritativeSequence()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 5 }
            };
            A.CallTo(() => _combatInteractionService.ExecuteRealTimeCombatCommandAsync(RealTimeCommandKind.Move,
                A<NetworkUnitMoveTo>._, null, null)).Returns(Task.FromResult(false));
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatCommandAccepted>(_networkClient);

            handler.Invoke(-1, new NotifyRealTimeCombatCommandAccepted
            {
                Epoch = 5,
                AuthoritativeSequence = 1,
                Kind = (int)RealTimeCommandKind.Move,
                Movement = new Networking.Messages.Contracts.NetworkUnitMoveTo
                {
                    InitiatorUnitId = "unit-a",
                    Destination = new Networking.Messages.Contracts.NetworkVector3(1, 2, 3)
                }
            });

            Assert.That(_multiplayerClient.Game.RealTimeCombat.LastAuthoritativeSequence, Is.Zero);
        }

        [Test]
        public void AcceptedRealTimeCommand_WaitsForMainThreadApplicationBeforeAdvancing()
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 5 }
            };
            A.CallTo(() => _combatInteractionService.ExecuteRealTimeCombatCommandAsync(RealTimeCommandKind.Move,
                A<NetworkUnitMoveTo>._, null, null)).Returns(completion.Task);
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatCommandAccepted>(_networkClient);

            handler.Invoke(-1, new NotifyRealTimeCombatCommandAccepted
            {
                Epoch = 5,
                AuthoritativeSequence = 1,
                Kind = (int)RealTimeCommandKind.Move,
                Movement = new Networking.Messages.Contracts.NetworkUnitMoveTo
                {
                    InitiatorUnitId = "unit-a",
                    Destination = new Networking.Messages.Contracts.NetworkVector3(1, 2, 3)
                }
            });

            Assert.That(_multiplayerClient.Game.RealTimeCombat.LastAuthoritativeSequence, Is.Zero);
            completion.SetResult(true);
            Assert.That(SpinWait.SpinUntil(() => _multiplayerClient.Game.RealTimeCombat.LastAuthoritativeSequence == 1,
                TimeSpan.FromSeconds(2)), Is.True);
        }

        [Test]
        public void CombatModeSwitch_PrepareCommitComplete_StaysPausedUntilFinalized()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running }
            };
            A.CallTo(() => _gameInteractionService.SetPauseAsync(true)).Returns(Task.CompletedTask);
            A.CallTo(() => _gameInteractionService.SetTurnBasedCombatModeAsync(false)).Returns(Task.CompletedTask);
            A.CallTo(() => _combatInteractionService.UpdateRealTimeCombatStateAsync(A<NetworkCombatState>._)).Returns(Task.CompletedTask);
            A.CallTo(() => _combatInteractionService.GetRealTimeCombatStateAsync()).Returns(Task.FromResult(new NetworkCombatState()));
            var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = new NetworkCombatState() });
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeModeTransition>(_networkClient);

            handler.Invoke(-1, new NotifyRealTimeModeTransition
            {
                Epoch = 1, Stage = (int)RealTimeModeTransitionStage.Prepare,
                Mode = (int)CombatMode.RealTimeWithPause
            });
            Assert.That(_multiplayerClient.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.PreparingModeSwitch));
            handler.Invoke(-1, new NotifyRealTimeModeTransition
            {
                Epoch = 1, Stage = (int)RealTimeModeTransitionStage.Commit,
                Mode = (int)CombatMode.RealTimeWithPause,
                CanonicalStateHash = hash,
                State = new Networking.Messages.Contracts.NetworkCombatState()
            });
            Assert.That(_multiplayerClient.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Synchronizing));
            A.CallTo(() => _gameInteractionService.SetPause(false)).MustNotHaveHappened();
            handler.Invoke(-1, new NotifyRealTimeModeTransition
            {
                Epoch = 1, Stage = (int)RealTimeModeTransitionStage.Complete,
                Mode = (int)CombatMode.RealTimeWithPause,
                CanonicalStateHash = hash
            });

            Assert.That(_multiplayerClient.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            A.CallTo(() => _gameInteractionService.SetPause(false)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeModeTransitionAck>.That.Matches(x => x.Stage == (int)RealTimeModeTransitionStage.Commit && x.Applied && x.CanonicalStateHash == hash)))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void RealTimeRepair_VerifyApplyResume_UsesPausedLocalFingerprint()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause }
            };
            A.CallTo(() => _gameInteractionService.SetPauseAsync(true)).Returns(Task.CompletedTask);
            A.CallTo(() => _combatInteractionService.UpdateRealTimeCombatStateAsync(A<NetworkCombatState>._)).Returns(Task.CompletedTask);
            A.CallTo(() => _combatInteractionService.GetRealTimeCombatStateAsync()).Returns(Task.FromResult(new NetworkCombatState()));
            var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = new NetworkCombatState() });
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRepair>(_networkClient);

            handler.Invoke(-1, new NotifyRealTimeRepair { Epoch = 1, Stage = (int)RealTimeRepairStage.Prepare, Mode = (int)CombatMode.RealTimeWithPause });
            handler.Invoke(-1, new NotifyRealTimeRepair { Epoch = 1, Stage = (int)RealTimeRepairStage.Verify, Mode = (int)CombatMode.RealTimeWithPause, CanonicalStateHash = hash });
            handler.Invoke(-1, new NotifyRealTimeRepair
            {
                Epoch = 1, Stage = (int)RealTimeRepairStage.Apply,
                Mode = (int)CombatMode.RealTimeWithPause,
                CanonicalStateHash = hash,
                State = new Networking.Messages.Contracts.NetworkCombatState()
            });

            Assert.That(_multiplayerClient.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeRepairAck>.That.Matches(x => x.Stage == (int)RealTimeRepairStage.Apply && x.Applied && x.CanonicalStateHash == hash)))
                .MustHaveHappenedOnceExactly();
            handler.Invoke(-1, new NotifyRealTimeRepair { Epoch = 1, Stage = (int)RealTimeRepairStage.Resume, Mode = (int)CombatMode.RealTimeWithPause, CanonicalStateHash = hash });
            Assert.That(_multiplayerClient.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
        }

        [Test]
        public void RealTimeModeAbort_AppliesFullCheckpointWhenPrepareWasLost()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.TurnBased, Epoch = 0 }
            };
            A.CallTo(() => _gameInteractionService.SetPauseAsync(true)).Returns(Task.CompletedTask);
            A.CallTo(() => _gameInteractionService.SetTurnBasedCombatModeAsync(true)).Returns(Task.CompletedTask);
            A.CallTo(() => _combatInteractionService.UpdateRealTimeCombatStateAsync(A<NetworkCombatState>._)).Returns(Task.CompletedTask);
            A.CallTo(() => _combatInteractionService.GetRealTimeCombatStateAsync()).Returns(Task.FromResult(new NetworkCombatState()));
            var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = new NetworkCombatState() });
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeModeTransition>(_networkClient);

            handler.Invoke(-1, new NotifyRealTimeModeTransition
            {
                Epoch = 1, Stage = (int)RealTimeModeTransitionStage.Abort,
                Mode = (int)CombatMode.TurnBased, CanonicalStateHash = hash,
                State = new Networking.Messages.Contracts.NetworkCombatState()
            });

            Assert.That(_multiplayerClient.Game.RealTimeCombat.Epoch, Is.EqualTo(1));
            Assert.That(_multiplayerClient.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeModeTransitionAck>.That.Matches(x =>
                x.Stage == (int)RealTimeModeTransitionStage.Abort && x.Applied))).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void RealTimeModePrepare_DuplicateIsAcknowledgedWithoutPausingTwice()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.TurnBased }
            };
            A.CallTo(() => _gameInteractionService.SetPauseAsync(true)).Returns(Task.CompletedTask);
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeModeTransition>(_networkClient);
            var prepare = new NotifyRealTimeModeTransition
            {
                Epoch = 1, Stage = (int)RealTimeModeTransitionStage.Prepare,
                Mode = (int)CombatMode.RealTimeWithPause
            };

            handler.Invoke(-1, prepare);
            handler.Invoke(-1, prepare);

            A.CallTo(() => _gameInteractionService.SetPauseAsync(true)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeModeTransitionAck>.That.Matches(x =>
                x.Stage == (int)RealTimeModeTransitionStage.Prepare && x.Applied))).MustHaveHappenedTwiceExactly();
        }

        [Test]
        public void RealTimeRepairPrepare_DuplicateIsAcknowledgedWithoutPausingTwice()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause }
            };
            A.CallTo(() => _gameInteractionService.SetPauseAsync(true)).Returns(Task.CompletedTask);
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRepair>(_networkClient);
            var prepare = new NotifyRealTimeRepair
            {
                Epoch = 1, Stage = (int)RealTimeRepairStage.Prepare,
                Mode = (int)CombatMode.RealTimeWithPause
            };

            handler.Invoke(-1, prepare);
            handler.Invoke(-1, prepare);

            A.CallTo(() => _gameInteractionService.SetPauseAsync(true)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeRepairAck>.That.Matches(x =>
                x.Stage == (int)RealTimeRepairStage.Prepare && x.Applied))).MustHaveHappenedTwiceExactly();
        }

        [Test]
        public void RealTimeRepairFallback_AppliesFullCheckpointWhenPrepareWasLost()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 0 }
            };
            A.CallTo(() => _gameInteractionService.SetPauseAsync(true)).Returns(Task.CompletedTask);
            A.CallTo(() => _gameInteractionService.SetTurnBasedCombatModeAsync(true)).Returns(Task.CompletedTask);
            A.CallTo(() => _combatInteractionService.UpdateRealTimeCombatStateAsync(A<NetworkCombatState>._)).Returns(Task.CompletedTask);
            A.CallTo(() => _combatInteractionService.GetRealTimeCombatStateAsync()).Returns(Task.FromResult(new NetworkCombatState()));
            var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = new NetworkCombatState() });
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRepair>(_networkClient);

            handler.Invoke(-1, new NotifyRealTimeRepair
            {
                Epoch = 2, Stage = (int)RealTimeRepairStage.Fallback,
                Mode = (int)CombatMode.TurnBased, CanonicalStateHash = hash,
                State = new Networking.Messages.Contracts.NetworkCombatState()
            });

            Assert.That(_multiplayerClient.Game.RealTimeCombat.Epoch, Is.EqualTo(2));
            Assert.That(_multiplayerClient.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.FallingBackToTurnBased));
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeRepairAck>.That.Matches(x =>
                x.Stage == (int)RealTimeRepairStage.Fallback && x.Applied))).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void RealTimeAI_ConsumesOnlyCurrentHostChoiceOnce()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 3 }
            };
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyAIActionSelected>(_networkClient);
            var choice = new NotifyAIActionSelected
            {
                RealTimeEpoch = 3,
                RealTimeSequence = 1,
                Action = new Networking.Messages.Contracts.NetworkAIAction { UnitId = "enemy-a", Id = "action-a" }
            };

            handler.Invoke(-1, choice);
            handler.Invoke(-1, choice);
            handler.Invoke(-1, new NotifyAIActionSelected
            {
                RealTimeEpoch = 2,
                RealTimeSequence = 2,
                Action = new Networking.Messages.Contracts.NetworkAIAction { UnitId = "enemy-a", Id = "old-action" }
            });

            Assert.That(_multiplayerClient.TryConsumeAuthoritativeRealTimeAIAction("enemy-a", out var action), Is.True);
            Assert.That(action.Id, Is.EqualTo("action-a"));
            Assert.That(_multiplayerClient.TryConsumeAuthoritativeRealTimeAIAction("enemy-a", out _), Is.False);
        }

        [Test]
        public void RealTimeAI_UnresolvableChoiceReportsEpochScopedFault()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 7 }
            };

            _multiplayerClient.ReportRealTimeAIActionFault("enemy-a", "spell-a", "target-not-found");

            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeAIActionFault>.That.Matches(x =>
                x.Epoch == 7 && x.UnitId == "enemy-a" && x.ActionId == "spell-a"
                && x.Reason == "target-not-found"))).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void RealTimeAI_ExplicitNoActionMarkerIsQueuedUntilConsumed()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 3 }
            };
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyAIActionSelected>(_networkClient);
            handler.Invoke(-1, new NotifyAIActionSelected
            {
                RealTimeEpoch = 3, RealTimeSequence = 1,
                Action = new Networking.Messages.Contracts.NetworkAIAction { UnitId = "enemy-a" }
            });

            Assert.That(_multiplayerClient.TryConsumeAuthoritativeRealTimeAIAction("enemy-a", out var consumed), Is.True);
            Assert.That(consumed.Id, Is.Null);
            Assert.That(_multiplayerClient.TryConsumeAuthoritativeRealTimeAIAction("enemy-a", out _), Is.False);
        }

        [Test]
        public void RealTimePausePolicy_UpdatesInTurnBasedWithoutChangingGamePause()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.TurnBased, Epoch = 4 }
            };
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimePauseChanged>(_networkClient);

            handler.Invoke(-1, new NotifyRealTimePauseChanged
            {
                Epoch = 4, Sequence = 1, Policy = (int)PausePolicy.HostOnly,
                PausedPlayers = [], ResumeVotes = [], IsPaused = false
            });

            Assert.That(_multiplayerClient.Game.RealTimeCombat.PausePolicy, Is.EqualTo(PausePolicy.HostOnly));
            A.CallTo(() => _gameInteractionService.SetPause(A<bool>._)).MustNotHaveHappened();
        }

        [Test]
        public void PreferredRealTimeMode_UsesLatestHostPreference()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp());
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatPreference>(_networkClient);

            handler.Invoke(-1, new NotifyRealTimeCombatPreference
            {
                Sequence = 2, PreferredMode = (int)CombatMode.RealTimeWithPause,
                PausePolicy = (int)PausePolicy.AnyoneFreeResume
            });
            handler.Invoke(-1, new NotifyRealTimeCombatPreference
            {
                Sequence = 1, PreferredMode = (int)CombatMode.TurnBased,
                PausePolicy = (int)PausePolicy.HostOnly
            });

            Assert.That(_multiplayerClient.Game.RealTimeCombat.PreferredMode, Is.EqualTo(CombatMode.RealTimeWithPause));
            Assert.That(_multiplayerClient.Game.RealTimeCombat.PausePolicy, Is.EqualTo(PausePolicy.AnyoneFreeResume));
        }

        [Test]
        public void RealTimePauseChange_IgnoresStaleStateAndAppliesNewState()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 7 }
            };
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimePauseChanged>(_networkClient);
            var pause = new NotifyRealTimePauseChanged
            {
                Epoch = 7,
                Sequence = 1,
                IsPaused = true,
                Policy = (int)PausePolicy.HostOnly,
                PausedPlayers = [-1]
            };

            handler.Invoke(-1, pause);
            handler.Invoke(-1, pause);

            Assert.That(_multiplayerClient.Game.RealTimeCombat.IsManualPauseActive, Is.True);
            Assert.That(_multiplayerClient.Game.RealTimeCombat.PausePolicy, Is.EqualTo(PausePolicy.HostOnly));
            A.CallTo(() => _gameInteractionService.SetPause(true)).MustHaveHappenedOnceExactly();
        }
    }
}
