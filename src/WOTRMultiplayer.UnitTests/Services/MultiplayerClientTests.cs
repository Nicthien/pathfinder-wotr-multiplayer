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

        [TestCase("current", 17, 5, true)]
        [TestCase("previous", 17, 5, false)]
        [TestCase("current", 16, 5, false)]
        [TestCase("current", 17, 4, false)]
        [TestCase(null, 17, 5, false)]
        public void HostDeath_RequiresCurrentSessionSaveAndEpoch(string sessionId, int saveSeed,
            long epoch, bool expected)
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "current", LoadedSaveSeed = 17,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 5 }
            };
            A.CallTo(() => _combatInteractionService.TryApplyRealTimeUnitDeathAsync(
                A<string>._, A<NetworkPlayer>._, A<Func<bool>>._)).Returns(Task.FromResult<bool?>(true));
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeUnitKilled>(_networkClient)(-1,
                new NotifyRealTimeUnitKilled
                {
                    SessionId = sessionId, LoadedSaveSeed = saveSeed, Epoch = epoch,
                    Sequence = 1, UnitId = "enemy"
                });
            var call = A.CallTo(() => _combatInteractionService.TryApplyRealTimeUnitDeathAsync(
                A<string>._, A<NetworkPlayer>._, A<Func<bool>>._));
            if (expected) call.MustHaveHappenedOnceExactly();
            else call.MustNotHaveHappened();
        }

        [Test]
        public void HostDeath_SaveReloadedBeforeDispatch_DoesNotApplyOrPauseReplacement()
        {
            var session = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "current", LoadedSaveSeed = 17,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 5 }
            };
            _multiplayerClient.Game = session;
            A.CallTo(() => _combatInteractionService.TryApplyRealTimeUnitDeathAsync(
                A<string>._, A<NetworkPlayer>._, A<Func<bool>>._))
                .ReturnsLazily((string _, NetworkPlayer player, Func<bool> canApply) =>
                {
                    session.LoadedSaveSeed++;
                    Assert.That(canApply(), Is.False);
                    return Task.FromException<bool?>(new InvalidOperationException("old-save-dispatch"));
                });
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeUnitKilled>(_networkClient)(-1,
                new NotifyRealTimeUnitKilled
                {
                    SessionId = "current", LoadedSaveSeed = 17, Epoch = 5, Sequence = 1, UnitId = "enemy"
                });
            Assert.That(session.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeCommandFault>._)).MustNotHaveHappened();
            A.CallTo(() => _gameInteractionService.SetPause(A<bool>._)).MustNotHaveHappened();
        }

        [Test]
        public void HostDeath_ReloadedSaveMayReuseSequenceButDuplicatesAreAppliedOnce()
        {
            var session = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "current", LoadedSaveSeed = 17,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 5 }
            };
            _multiplayerClient.Game = session;
            A.CallTo(() => _combatInteractionService.TryApplyRealTimeUnitDeathAsync(
                A<string>._, A<NetworkPlayer>._, A<Func<bool>>._)).Returns(Task.FromResult<bool?>(true));
            var receive = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeUnitKilled>(_networkClient);
            foreach (var seed in new[] { 17, 17, 18, 18 })
            {
                session.LoadedSaveSeed = seed;
                receive(-1, new NotifyRealTimeUnitKilled
                {
                    SessionId = "current", LoadedSaveSeed = seed, Epoch = 5, Sequence = 1, UnitId = "enemy"
                });
            }
            A.CallTo(() => _combatInteractionService.TryApplyRealTimeUnitDeathAsync(
                A<string>._, A<NetworkPlayer>._, A<Func<bool>>._)).MustHaveHappenedTwiceExactly();
        }

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
        public void TurnEndCheckpointFailure_ReportsFailureAndBlocksSavingUntilSuccessfulRetry()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            A.CallTo(() => _combatInteractionService.IsCombatTurnFinished()).Returns(true);
            A.CallTo(() => _combatInteractionService.UpdateUnitsAsync(
                    A<System.Collections.Generic.List<Entities.Units.NetworkUnit>>._, true))
                .ReturnsNextFromSequence(Task.FromResult(false), Task.FromResult(true));
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Combat = new NetworkCombat
                {
                    Stage = NetworkCombatStage.Running,
                    Turn = new NetworkCombatTurn { UnitId = "hero" }
                }
            };
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyCombatTurnEndSynchronizationRequired>(_networkClient);
            var checkpoint = new NotifyCombatTurnEndSynchronizationRequired
            {
                UnitId = "hero", SynchronizationSequence = 8
            };

            handler(NetworkConstants.HostPlayerId, checkpoint);
            Assert.That(_multiplayerClient.IsSavingBlockedByCombatSync, Is.True);
            A.CallTo(() => _networkClient.Broadcast(A<ClientCombatTurnEndSynchronized>.That.Matches(x =>
                x.UnitId == "hero" && x.SynchronizationSequence == 8 && !x.Succeeded)))
                .MustHaveHappenedOnceExactly();
            FakeUtils.GetNetworkReceiverHandler<NotifyCombatTurnEnded>(_networkClient)(
                NetworkConstants.HostPlayerId, new NotifyCombatTurnEnded
                {
                    UnitId = "hero", SynchronizationSequence = 7
                });
            Assert.That(_multiplayerClient.IsSavingBlockedByCombatSync, Is.True);

            handler(NetworkConstants.HostPlayerId, checkpoint);
            Assert.That(_multiplayerClient.IsSavingBlockedByCombatSync, Is.False);
            A.CallTo(() => _networkClient.Broadcast(A<ClientCombatTurnEndSynchronized>.That.Matches(x =>
                x.UnitId == "hero" && x.SynchronizationSequence == 8 && x.Succeeded)))
                .MustHaveHappenedOnceExactly();
            FakeUtils.GetNetworkReceiverHandler<NotifyCombatTurnEnded>(_networkClient)(
                NetworkConstants.HostPlayerId, new NotifyCombatTurnEnded
                {
                    UnitId = "hero", SynchronizationSequence = 8
                });
            Assert.That(_multiplayerClient.Game.Combat.Turn.Stage, Is.EqualTo(NetworkCombatTurnStage.Ended));
        }

        [Test]
        public void TurnEndCheckpointForDifferentUnit_ReportsFailureWithoutApplyingUnits()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Combat = new NetworkCombat
                {
                    Stage = NetworkCombatStage.Running,
                    Turn = new NetworkCombatTurn { UnitId = "client-unit" }
                }
            };

            FakeUtils.GetNetworkReceiverHandler<NotifyCombatTurnEndSynchronizationRequired>(_networkClient)(
                NetworkConstants.HostPlayerId, new NotifyCombatTurnEndSynchronizationRequired
                {
                    UnitId = "host-unit", SynchronizationSequence = 8
                });

            Assert.That(_multiplayerClient.IsSavingBlockedByCombatSync, Is.True);
            A.CallTo(() => _combatInteractionService.UpdateUnitsAsync(
                A<System.Collections.Generic.List<Entities.Units.NetworkUnit>>._, true))
                .MustNotHaveHappened();
            A.CallTo(() => _networkClient.Broadcast(A<ClientCombatTurnEndSynchronized>.That.Matches(x =>
                !x.Succeeded && x.SynchronizationSequence == 8
                    && x.FailureReason == "wrong-current-unit")))
                .MustHaveHappenedOnceExactly();
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
            Assert.That(_multiplayerClient.Game.RealTimeCombat.Phase,
                Is.EqualTo(CombatSynchronizationPhase.Repairing));
            A.CallTo(() => _gameInteractionService.SetPause(true)).MustHaveHappenedOnceExactly();
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
            A.CallTo(() => _gameInteractionService.TrySetTurnBasedCombatModeAsync(A<bool>._, A<Func<bool>>._))
                .ReturnsLazily((bool _, Func<bool> canApply) => Task.FromResult(canApply()));
            _levelingInteractionService = A.Fake<ILevelingInteractionService>();
            _playerNotificationService = A.Fake<IPlayerNotificationService>();
            _dialogInteractionService = A.Fake<IDialogInteractionService>();
            _globalMapInteractionService = A.Fake<IGlobalMapInteractionService>();
            _pingInteractionService = A.Fake<IPingInteractionService>();
            _combatInteractionService = A.Fake<ICombatInteractionService>();
            A.CallTo(() => _combatInteractionService.TryPauseAtProjectileSafePointAsync(
                    A<Func<bool>>._, A<Action>._))
                .ReturnsLazily((Func<bool> canPause, Action pause) =>
                {
                    if (!canPause()) return Task.FromResult(false);
                    pause();
                    return Task.FromResult(true);
                });
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
            A.CallTo(() => _combatInteractionService.TryApplyAndReadRealTimeCombatStateAsync(
                    A<NetworkCombatState>._, A<Func<bool>>._))
                .Returns(Task.FromResult(new NetworkCombatState()));
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
            A.CallTo(() => _combatInteractionService.TryApplyAndReadRealTimeCombatStateAsync(
                A<NetworkCombatState>._, A<Func<bool>>._)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _combatInteractionService.UpdateRealTimeCombatStateAsync(A<NetworkCombatState>._))
                .MustNotHaveHappened();
        }

        [Test]
        public void RealTimeSnapshot_ReplacedSessionBeforeQueuedApplyIsNotAcknowledged()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                RealTimeCombat = new NetworkRealTimeCombatState
                {
                    Mode = CombatMode.RealTimeWithPause, Epoch = 2
                }
            };
            var replacement = new NetworkGame(new NetworkGameStartUp());
            A.CallTo(() => _combatInteractionService.TryApplyAndReadRealTimeCombatStateAsync(
                A<NetworkCombatState>._, A<Func<bool>>._))
                .ReturnsLazily((NetworkCombatState _, Func<bool> canApply) =>
                {
                    _multiplayerClient.Game = replacement;
                    Assert.That(canApply(), Is.False);
                    return Task.FromResult<NetworkCombatState>(null);
                });

            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(_networkClient)(-1,
                new NotifyRealTimeCombatSnapshot
                {
                    Epoch = 2, Sequence = 1,
                    State = new Networking.Messages.Contracts.NetworkCombatState()
                });

            Assert.That(_multiplayerClient.Game, Is.SameAs(replacement));
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeStateHash>._))
                .MustNotHaveHappened();
        }

        [Test]
        public void RealTimeSnapshot_FailedApplication_ReportsFailureToHost()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 2 }
            };
            A.CallTo(() => _combatInteractionService.TryApplyAndReadRealTimeCombatStateAsync(
                    A<NetworkCombatState>._, A<Func<bool>>._))
                .Returns(Task.FromException<NetworkCombatState>(new InvalidOperationException("missing unit")));
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

        [TestCase(-1, "presentation-session", 7, 5, true, false)]
        [TestCase(42, "presentation-session", 7, 5, false, false)]
        [TestCase(-1, "old-session", 7, 5, false, false)]
        [TestCase(-1, "presentation-session", 6, 5, false, false)]
        [TestCase(-1, "presentation-session", 7, 4, false, false)]
        [TestCase(-1, "presentation-session", 7, 5, true, true)]
        [TestCase(42, "presentation-session", 7, 5, false, true)]
        [TestCase(-1, "old-session", 7, 5, false, true)]
        [TestCase(-1, "presentation-session", 6, 5, false, true)]
        [TestCase(-1, "presentation-session", 7, 4, false, true)]
        public void ActionPresentation_RequiresHostAndCurrentSession(long sender, string sessionId,
            int saveSeed, long epoch, bool expected, bool ability)
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "presentation-session", LoadedSaveSeed = 7,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 5 }
            };
            A.CallTo(() => _combatInteractionService.TryPresentRealTimeActionAsync(
                A<NotifyRealTimeAuthoritativeEvent>._, A<Func<bool>>._)).Returns(Task.FromResult<bool?>(true));
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeAuthoritativeEvent>(_networkClient);
            var frame = new NotifyRealTimeAuthoritativeEvent
            {
                SessionId = sessionId, LoadedSaveSeed = saveSeed, Epoch = epoch, Sequence = 1,
                EventId = "5:1", UnitId = "unit-a", ActionId = "action-a",
                Kind = (int)(ability ? RealTimeAuthoritativeEventKind.AbilityPresentation : RealTimeAuthoritativeEventKind.AttackPresentation),
                Attack = ability ? null : new RealTimeAttackPresentationPayload(),
                Ability = ability ? new RealTimeAbilityPresentationPayload() : null
            };
            handler(sender, frame);
            handler(sender, frame);
            var call = A.CallTo(() => _combatInteractionService.TryPresentRealTimeActionAsync(
                A<NotifyRealTimeAuthoritativeEvent>._, A<Func<bool>>._));
            if (RealTimePresentationRuntime.Enabled && expected) call.MustHaveHappenedOnceExactly();
            else call.MustNotHaveHappened();
            A.CallTo(() => _combatInteractionService.TryExecuteRealTimeCombatCommandAsync(
                A<RealTimeCommandKind>._, A<NetworkUnitMoveTo>._, A<NetworkUnitAttack>._,
                A<NetworkAbilityUse>._, A<Func<bool>>._)).MustNotHaveHappened();
        }

        [TestCase(-1, "presentation-session", 7, 5, true)]
        [TestCase(42, "presentation-session", 7, 5, false)]
        [TestCase(-1, "old-session", 7, 5, false)]
        [TestCase(-1, "presentation-session", 6, 5, false)]
        [TestCase(-1, "presentation-session", 7, 4, false)]
        public void ProjectilePresentation_RequiresHostAndCurrentSession(long sender, string sessionId,
            int saveSeed, long epoch, bool expected)
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "presentation-session", LoadedSaveSeed = 7,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 5 }
            };
            A.CallTo(() => _combatInteractionService.TryPresentRealTimeActionAsync(
                A<NotifyRealTimeAuthoritativeEvent>._, A<Func<bool>>._)).Returns(Task.FromResult<bool?>(true));
            var receive = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeAuthoritativeEvent>(_networkClient);
            var frame = new NotifyRealTimeAuthoritativeEvent
            {
                SessionId = sessionId, LoadedSaveSeed = saveSeed, Epoch = epoch, Sequence = 1,
                EventId = "5:1", ActionId = "arrow", Kind = (int)RealTimeAuthoritativeEventKind.ProjectilePresentation,
                Projectile = new RealTimeProjectilePresentationPayload
                    { Stage = 1, State = new Networking.Messages.Contracts.NetworkProjectileState { Id = "arrow" } }
            };
            receive(sender, frame);
            receive(sender, frame);
            var call = A.CallTo(() => _combatInteractionService.TryPresentRealTimeActionAsync(
                A<NotifyRealTimeAuthoritativeEvent>._, A<Func<bool>>._));
            if (RealTimePresentationRuntime.Enabled && expected) call.MustHaveHappenedOnceExactly();
            else call.MustNotHaveHappened();
            A.CallTo(() => _combatInteractionService.TryExecuteRealTimeCombatCommandAsync(
                A<RealTimeCommandKind>._, A<NetworkUnitMoveTo>._, A<NetworkUnitAttack>._,
                A<NetworkAbilityUse>._, A<Func<bool>>._)).MustNotHaveHappened();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ActionPresentation_ReplacedSessionBeforeRenderingIsIgnored(bool ability)
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "presentation-session", LoadedSaveSeed = 7,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 5 }
            };
            var original = _multiplayerClient.Game;
            var replacement = new NetworkGame(new NetworkGameStartUp());
            A.CallTo(() => _combatInteractionService.TryPresentRealTimeActionAsync(
                A<NotifyRealTimeAuthoritativeEvent>._, A<Func<bool>>._))
                .ReturnsLazily((NotifyRealTimeAuthoritativeEvent _, Func<bool> canPresent) =>
                {
                    _multiplayerClient.Game = replacement;
                    Assert.That(canPresent(), Is.False);
                    return Task.FromResult<bool?>(null);
                });
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeAuthoritativeEvent>(_networkClient)(-1,
                new NotifyRealTimeAuthoritativeEvent
                {
                    SessionId = original.Id, LoadedSaveSeed = 7, Epoch = 5, Sequence = 1,
                    EventId = "5:1", UnitId = "unit-a", ActionId = "action-a",
                    Kind = (int)(ability ? RealTimeAuthoritativeEventKind.AbilityPresentation : RealTimeAuthoritativeEventKind.AttackPresentation),
                    Attack = ability ? null : new RealTimeAttackPresentationPayload(),
                    Ability = ability ? new RealTimeAbilityPresentationPayload() : null
                });
            Assert.That(_multiplayerClient.Game, Is.SameAs(RealTimePresentationRuntime.Enabled ? replacement : original));
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeCommandFault>._)).MustNotHaveHappened();
        }

        [Test]
        public void AcceptedRealTimeCommand_IsAppliedOnceAndStaleRepeatIsIgnored()
        {
            A.CallTo(() => _combatInteractionService.TryExecuteRealTimeCombatCommandAsync(RealTimeCommandKind.Move,
                A<NetworkUnitMoveTo>._, null, null, A<Func<bool>>._)).Returns(Task.FromResult<bool?>(true));
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

            if (RealTimePresentationRuntime.Enabled)
            {
                A.CallTo(() => _combatInteractionService.TryExecuteRealTimeCombatCommandAsync(
                    A<RealTimeCommandKind>._, A<NetworkUnitMoveTo>._, A<NetworkUnitAttack>._,
                    A<NetworkAbilityUse>._, A<Func<bool>>._)).MustNotHaveHappened();
                Assert.That(_multiplayerClient.Game.RealTimeCombat.LastAuthoritativeSequence, Is.EqualTo(1));
                return;
            }
            A.CallTo(() => _combatInteractionService.TryExecuteRealTimeCombatCommandAsync(RealTimeCommandKind.Move,
                A<NetworkUnitMoveTo>.That.Matches(x => x.InitiatorUnitId == "unit-a"), null, null, A<Func<bool>>._))
                .MustHaveHappenedOnceExactly();
            Assert.That(_multiplayerClient.Game.RealTimeCombat.LastAuthoritativeSequence, Is.EqualTo(1));
        }

        [Test]
        public void FailedRealTimeCommand_DoesNotAdvanceAuthoritativeSequence()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 5 }
            };
            A.CallTo(() => _combatInteractionService.TryExecuteRealTimeCombatCommandAsync(RealTimeCommandKind.Move,
                A<NetworkUnitMoveTo>._, null, null, A<Func<bool>>._)).Returns(Task.FromResult<bool?>(false));
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

            if (RealTimePresentationRuntime.Enabled)
            {
                Assert.That(_multiplayerClient.Game.RealTimeCombat.LastAuthoritativeSequence, Is.EqualTo(1));
                Assert.That(_multiplayerClient.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
                A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeCommandFault>._)).MustNotHaveHappened();
                return;
            }
            Assert.That(_multiplayerClient.Game.RealTimeCombat.LastAuthoritativeSequence, Is.Zero);
            Assert.That(_multiplayerClient.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeCommandFault>.That.Matches(x =>
                x.Epoch == 5 && x.AuthoritativeSequence == 1
                    && x.Reason == "authoritative-command-rejected"))).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void AcceptedRealTimeCommand_ReplacedSessionBeforeQueuedExecutionIsIgnored()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState
                {
                    Mode = CombatMode.RealTimeWithPause, Epoch = 5
                }
            };
            var replacement = new NetworkGame(new NetworkGameStartUp());
            A.CallTo(() => _combatInteractionService.TryExecuteRealTimeCombatCommandAsync(
                RealTimeCommandKind.Move, A<NetworkUnitMoveTo>._, null, null, A<Func<bool>>._))
                .ReturnsLazily((RealTimeCommandKind _, NetworkUnitMoveTo movement,
                    NetworkUnitAttack attack, NetworkAbilityUse ability, Func<bool> canExecute) =>
                {
                    _multiplayerClient.Game = replacement;
                    Assert.That(canExecute(), Is.False);
                    return Task.FromResult<bool?>(null);
                });

            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatCommandAccepted>(_networkClient)(-1,
                new NotifyRealTimeCombatCommandAccepted
                {
                    Epoch = 5, AuthoritativeSequence = 1,
                    Kind = (int)RealTimeCommandKind.Move,
                    Movement = new Networking.Messages.Contracts.NetworkUnitMoveTo
                    {
                        InitiatorUnitId = "unit-a",
                        Destination = new Networking.Messages.Contracts.NetworkVector3(1, 2, 3)
                    }
                });

            if (RealTimePresentationRuntime.Enabled)
            {
                // No local execution callback is scheduled by an acceptance in presentation mode.
                Assert.That(_multiplayerClient.Game, Is.Not.SameAs(replacement));
                Assert.That(_multiplayerClient.Game.RealTimeCombat.LastAuthoritativeSequence, Is.EqualTo(1));
                A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeCommandFault>._)).MustNotHaveHappened();
                return;
            }
            Assert.That(_multiplayerClient.Game, Is.SameAs(replacement));
            Assert.That(replacement.RealTimeCombat.LastAuthoritativeSequence, Is.Zero);
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeCommandFault>._))
                .MustNotHaveHappened();
        }

        [Test]
        public void AcceptedRealTimeCommand_WaitsForMainThreadApplicationBeforeAdvancing()
        {
            var completion = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 5 }
            };
            A.CallTo(() => _combatInteractionService.TryExecuteRealTimeCombatCommandAsync(RealTimeCommandKind.Move,
                A<NetworkUnitMoveTo>._, null, null, A<Func<bool>>._)).Returns(completion.Task);
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

            if (RealTimePresentationRuntime.Enabled)
            {
                Assert.That(_multiplayerClient.Game.RealTimeCombat.LastAuthoritativeSequence, Is.EqualTo(1));
                Assert.That(completion.Task.IsCompleted, Is.False);
                return;
            }
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
            A.CallTo(() => _combatInteractionService.TryApplyAndReadRealTimeCombatStateAsync(
                    A<NetworkCombatState>._, A<Func<bool>>._))
                .Returns(Task.FromResult(new NetworkCombatState()));
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
        public void CombatModeSwitch_ReplacedSessionDuringPauseDoesNotAcknowledgeOldPrepare()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running }
            };
            var replacement = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running }
            };
            A.CallTo(() => _combatInteractionService.TryPauseAtProjectileSafePointAsync(
                    A<Func<bool>>._, A<Action>._))
                .ReturnsLazily((Func<bool> canPause, Action pause) =>
                {
                    _multiplayerClient.Game = replacement;
                    return Task.FromResult(false);
                });

            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeModeTransition>(_networkClient)(-1,
                new NotifyRealTimeModeTransition
                {
                    Epoch = 1, Stage = (int)RealTimeModeTransitionStage.Prepare,
                    Mode = (int)CombatMode.RealTimeWithPause
                });

            Assert.That(_multiplayerClient.Game, Is.SameAs(replacement));
            Assert.That(replacement.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeModeTransitionAck>._))
                .MustNotHaveHappened();
        }

        [Test]
        public void CombatModeSwitch_ReplacedSessionBeforeQueuedModeChangeDoesNotAcknowledgeCommit()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running }
            };
            A.CallTo(() => _gameInteractionService.SetPauseAsync(true)).Returns(Task.CompletedTask);
            A.CallTo(() => _combatInteractionService.TryApplyAndReadRealTimeCombatStateAsync(
                    A<NetworkCombatState>._, A<Func<bool>>._))
                .Returns(Task.FromResult(new NetworkCombatState()));
            var modeChange = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<bool> capturedGuard = null;
            A.CallTo(() => _gameInteractionService.TrySetTurnBasedCombatModeAsync(false, A<Func<bool>>._))
                .ReturnsLazily((bool _, Func<bool> guard) =>
                {
                    capturedGuard = guard;
                    return modeChange.Task;
                });
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeModeTransition>(_networkClient);
            handler.Invoke(-1, new NotifyRealTimeModeTransition
            {
                Epoch = 1, Stage = (int)RealTimeModeTransitionStage.Prepare,
                Mode = (int)CombatMode.RealTimeWithPause
            });
            var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = new NetworkCombatState() });
            handler.Invoke(-1, new NotifyRealTimeModeTransition
            {
                Epoch = 1, Stage = (int)RealTimeModeTransitionStage.Commit,
                Mode = (int)CombatMode.RealTimeWithPause,
                CanonicalStateHash = hash,
                State = new Networking.Messages.Contracts.NetworkCombatState()
            });
            Assert.That(capturedGuard, Is.Not.Null);
            Assert.That(capturedGuard(), Is.True);

            var replacement = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Epoch = 1 }
            };
            _multiplayerClient.Game = replacement;
            Assert.That(capturedGuard(), Is.False);
            modeChange.SetResult(true);
            var gate = (SemaphoreSlim)typeof(MultiplayerClient)
                .GetField("_realTimeSnapshotGate", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)
                .GetValue(_multiplayerClient);
            Assert.That(SpinWait.SpinUntil(() => gate.CurrentCount == 1, TimeSpan.FromSeconds(2)), Is.True);

            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeModeTransitionAck>.That.Matches(x =>
                x.Stage == (int)RealTimeModeTransitionStage.Commit))).MustNotHaveHappened();
            Assert.That(replacement.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
        }

        [Test]
        public void CombatModeSwitch_WaitsForLocalProjectileBeforePrepareAcknowledgement()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause }
            };
            var checks = 0;
            var acknowledged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            A.CallTo(() => _combatInteractionService.TryPauseAtProjectileSafePointAsync(
                    A<Func<bool>>._, A<Action>._))
                .ReturnsLazily((Func<bool> canPause, Action pause) =>
                {
                    if (++checks == 1) return Task.FromResult(false);
                    Assert.That(canPause(), Is.True);
                    pause();
                    return Task.FromResult(true);
                });
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeModeTransitionAck>.That.Matches(x =>
                x.Stage == (int)RealTimeModeTransitionStage.Prepare && x.Applied)))
                .Invokes(_ => acknowledged.TrySetResult(true));

            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeModeTransition>(_networkClient)(-1,
                new NotifyRealTimeModeTransition
                {
                    Epoch = 1, Stage = (int)RealTimeModeTransitionStage.Prepare,
                    Mode = (int)CombatMode.TurnBased
                });

            Assert.That(acknowledged.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(checks, Is.EqualTo(2));
            A.CallTo(() => _gameInteractionService.SetPause(true)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void RealTimeRepair_ReplacedSessionDuringPauseDoesNotAcknowledgeOldPrepare()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause }
            };
            var replacement = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause }
            };
            A.CallTo(() => _gameInteractionService.SetPauseAsync(true)).ReturnsLazily(() =>
            {
                _multiplayerClient.Game = replacement;
                return Task.CompletedTask;
            });

            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRepair>(_networkClient)(-1,
                new NotifyRealTimeRepair
                {
                    Epoch = 1, Stage = (int)RealTimeRepairStage.Prepare,
                    Mode = (int)CombatMode.RealTimeWithPause
                });

            Assert.That(_multiplayerClient.Game, Is.SameAs(replacement));
            Assert.That(replacement.RealTimeCombat.Epoch, Is.Zero);
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeRepairAck>._))
                .MustNotHaveHappened();
        }

        [Test]
        public void RealTimeRepair_ReplacedSessionDuringVerificationDoesNotAcknowledgeOldState()
        {
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState
                {
                    Mode = CombatMode.RealTimeWithPause, Epoch = 1,
                    Phase = CombatSynchronizationPhase.Repairing
                }
            };
            var replacement = new NetworkGame(new NetworkGameStartUp())
            {
                LocalPlayerId = 42,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause }
            };
            A.CallTo(() => _combatInteractionService.GetRealTimeCombatStateAsync()).ReturnsLazily(() =>
            {
                _multiplayerClient.Game = replacement;
                return Task.FromResult(new NetworkCombatState());
            });

            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRepair>(_networkClient)(-1,
                new NotifyRealTimeRepair
                {
                    Epoch = 1, Stage = (int)RealTimeRepairStage.Verify,
                    Mode = (int)CombatMode.RealTimeWithPause
                });

            Assert.That(_multiplayerClient.Game, Is.SameAs(replacement));
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeRepairAck>._))
                .MustNotHaveHappened();
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
            A.CallTo(() => _combatInteractionService.TryApplyAndReadRealTimeCombatStateAsync(
                    A<NetworkCombatState>._, A<Func<bool>>._))
                .Returns(Task.FromResult(new NetworkCombatState()));
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
            A.CallTo(() => _combatInteractionService.TryApplyAndReadRealTimeCombatStateAsync(
                    A<NetworkCombatState>._, A<Func<bool>>._))
                .Returns(Task.FromResult(new NetworkCombatState()));
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

            A.CallTo(() => _gameInteractionService.SetPause(true)).MustHaveHappenedOnceExactly();
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
            A.CallTo(() => _combatInteractionService.TryApplyAndReadRealTimeCombatStateAsync(
                    A<NetworkCombatState>._, A<Func<bool>>._))
                .Returns(Task.FromResult(new NetworkCombatState()));
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
        public void RealTimeAI_ReorderedHostChoicesWaitForMissingSequence()
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
                RealTimeEpoch = 3, RealTimeSequence = 2,
                Action = new Networking.Messages.Contracts.NetworkAIAction { UnitId = "enemy-a", Id = "second" }
            });
            Assert.That(_multiplayerClient.TryConsumeAuthoritativeRealTimeAIAction("enemy-a", out _), Is.False);

            handler.Invoke(-1, new NotifyAIActionSelected
            {
                RealTimeEpoch = 3, RealTimeSequence = 1,
                Action = new Networking.Messages.Contracts.NetworkAIAction { UnitId = "enemy-a", Id = "first" }
            });
            Assert.That(_multiplayerClient.TryConsumeAuthoritativeRealTimeAIAction("enemy-a", out var first), Is.True);
            Assert.That(first.Id, Is.EqualTo("first"));
            Assert.That(_multiplayerClient.TryConsumeAuthoritativeRealTimeAIAction("enemy-a", out var second), Is.True);
            Assert.That(second.Id, Is.EqualTo("second"));
            Assert.That(_multiplayerClient.TryConsumeAuthoritativeRealTimeAIAction("enemy-a", out _), Is.False);
        }

        [Test]
        public void RealTimeAI_SequenceOverflowEntersRepairBarrier()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 3 }
            };

            FakeUtils.GetNetworkReceiverHandler<NotifyAIActionSelected>(_networkClient)(-1,
                new NotifyAIActionSelected
                {
                    RealTimeEpoch = 3, RealTimeSequence = 130,
                    Action = new Networking.Messages.Contracts.NetworkAIAction { UnitId = "enemy-a", Id = "future" }
                });

            Assert.That(_multiplayerClient.Game.RealTimeCombat.Phase,
                Is.EqualTo(CombatSynchronizationPhase.Repairing));
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeAIActionFault>.That.Matches(x =>
                x.Epoch == 3 && x.ActionId == "future"
                && x.Reason == "host-choice-sequence-overflow"))).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void RealTimeAI_UnfilledSequenceGapTimesOutIntoRepair()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            A.CallTo(() => _gameInteractionService.SetPauseAsync(true)).Returns(Task.CompletedTask);
            var faultSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeAIActionFault>.That.Matches(x =>
                x.Epoch == 3 && x.Reason == "host-choice-sequence-gap")))
                .Invokes(_ => faultSent.TrySetResult(true));
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState { Mode = CombatMode.RealTimeWithPause, Epoch = 3 }
            };

            FakeUtils.GetNetworkReceiverHandler<NotifyAIActionSelected>(_networkClient)(-1,
                new NotifyAIActionSelected
                {
                    RealTimeEpoch = 3, RealTimeSequence = 2,
                    Action = new Networking.Messages.Contracts.NetworkAIAction { UnitId = "enemy-a", Id = "future" }
                });

            Assert.That(faultSent.Task.Wait(TimeSpan.FromSeconds(7)), Is.True);
            Assert.That(_multiplayerClient.Game.RealTimeCombat.Phase,
                Is.EqualTo(CombatSynchronizationPhase.Repairing));
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeAIActionFault>.That.Matches(x =>
                x.Epoch == 3 && x.Reason == "host-choice-sequence-gap"))).MustHaveHappenedOnceExactly();
            A.CallTo(() => _gameInteractionService.SetPauseAsync(true)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _gameInteractionService.SetPause(true)).MustNotHaveHappened();
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

            Assert.That(_multiplayerClient.Game.RealTimeCombat.Phase,
                Is.EqualTo(CombatSynchronizationPhase.Repairing));
            A.CallTo(() => _gameInteractionService.SetPause(true)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeAIActionFault>.That.Matches(x =>
                x.Epoch == 7 && x.UnitId == "enemy-a" && x.ActionId == "spell-a"
                && x.Reason == "target-not-found"))).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void RealTimeCriticalFault_DuplicateModeCompleteCannotReopenClientBarrier()
        {
            A.CallTo(() => _networkClient.IsActive).Returns(true);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running },
                RealTimeCombat = new NetworkRealTimeCombatState
                {
                    Mode = CombatMode.RealTimeWithPause,
                    Epoch = 7,
                    CanonicalStateHash = 123
                }
            };
            _multiplayerClient.ReportRealTimeAIActionFault("enemy-a", "spell-a", "target-not-found");
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeModeTransition>(_networkClient)(-1,
                new NotifyRealTimeModeTransition
                {
                    Epoch = 7, Stage = (int)RealTimeModeTransitionStage.Complete,
                    Mode = (int)CombatMode.RealTimeWithPause, CanonicalStateHash = 123
                });
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRepair>(_networkClient)(-1,
                new NotifyRealTimeRepair
                {
                    Epoch = 7, Stage = (int)RealTimeRepairStage.Resume,
                    Mode = (int)CombatMode.RealTimeWithPause, CanonicalStateHash = 123
                });

            Assert.That(_multiplayerClient.Game.RealTimeCombat.Phase,
                Is.EqualTo(CombatSynchronizationPhase.Repairing));
            A.CallTo(() => _gameInteractionService.SetPause(false)).MustNotHaveHappened();
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeModeTransitionAck>.That.Matches(x =>
                x.Stage == (int)RealTimeModeTransitionStage.Complete))).MustNotHaveHappened();
            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeRepairAck>.That.Matches(x =>
                x.Stage == (int)RealTimeRepairStage.Resume))).MustNotHaveHappened();
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

        [Test]
        public void RealTimePauseChange_CannotUnpauseCriticalRepairBarrier()
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
            _multiplayerClient.ReportRealTimeAIActionFault("enemy-a", "spell-a", "target-not-found");

            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimePauseChanged>(_networkClient)(-1,
                new NotifyRealTimePauseChanged
                {
                    Epoch = 7, Sequence = 1, IsPaused = false,
                    Policy = (int)PausePolicy.AnyoneFreeResume, PausedPlayers = []
                });

            Assert.That(_multiplayerClient.Game.RealTimeCombat.Phase,
                Is.EqualTo(CombatSynchronizationPhase.Repairing));
            A.CallTo(() => _gameInteractionService.SetPause(true)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _gameInteractionService.SetPause(false)).MustNotHaveHappened();
        }
    }
}
