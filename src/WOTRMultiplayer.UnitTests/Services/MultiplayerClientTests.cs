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
    }
}
