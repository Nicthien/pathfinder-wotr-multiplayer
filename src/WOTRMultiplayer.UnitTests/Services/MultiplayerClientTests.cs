using System;
using AutoMapper;
using System.Threading.Tasks;
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
            A.CallTo(() => _combatInteractionService.UpdateCombatStateAsync(A<NetworkCombatState>._, A<System.Collections.Generic.List<NetworkAreaEffect>>._, false))
                .Returns(Task.CompletedTask);
            A.CallTo(() => _combatInteractionService.GetRealTimeCombatStateAsync()).Returns(Task.FromResult(new NetworkCombatState()));
            var handler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(_networkClient);

            handler.Invoke(-1, new NotifyRealTimeCombatSnapshot
            {
                Epoch = 2,
                Sequence = 1,
                State = new WOTRMultiplayer.Networking.Messages.Contracts.NetworkCombatState()
            });

            A.CallTo(() => _networkClient.Broadcast(A<ClientRealTimeStateHash>.That.Matches(x => x.Epoch == 2 && x.Sequence == 1)))
                .MustHaveHappenedOnceExactly();
            Assert.That(_multiplayerClient.Game.RealTimeCombat.LastAuthoritativeSequence, Is.EqualTo(1));
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

            A.CallTo(() => _combatInteractionService.MoveUnit(A<NetworkUnitMoveTo>.That.Matches(x => x.InitiatorUnitId == "unit-a")))
                .MustHaveHappenedOnceExactly();
            Assert.That(_multiplayerClient.Game.RealTimeCombat.LastAuthoritativeSequence, Is.EqualTo(1));
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
