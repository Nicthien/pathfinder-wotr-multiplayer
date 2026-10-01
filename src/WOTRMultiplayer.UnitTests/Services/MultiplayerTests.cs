using FakeItEasy;
using System;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Abstractions.Hotkeys;
using WOTRMultiplayer.Abstractions.Random;
using WOTRMultiplayer.Abstractions.UI;
using WOTRMultiplayer.Abstractions.UI.Controllers;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class MultiplayerTests
    {
        private Multiplayer _multiplayer;
        private ILogger<Multiplayer> _logger;
        private IUIFactory _uiFactory;
        private ILobbyWindowController _lobbyWindowController;
        private IMultiplayerHost _multiplayerHost;
        private IMultiplayerClient _multiplayerClient;
        private IGameInteractionService _gameInteractionService;
        private IValueGenerator _idGenerator;
        private IMultiplayerActorAccessor _multiplerActorAccessor;
        private IHotkeysService _hotkeysService;

        [SetUp]
        public void SetUp()
        {
            _logger = A.Fake<ILogger<Multiplayer>>();
            _uiFactory = A.Fake<IUIFactory>();
            _lobbyWindowController = A.Fake<ILobbyWindowController>();
            _multiplayerHost = A.Fake<IMultiplayerHost>();
            _multiplayerClient = A.Fake<IMultiplayerClient>();
            _gameInteractionService = A.Fake<IGameInteractionService>();
            _idGenerator = A.Fake<IValueGenerator>();
            _multiplerActorAccessor = A.Fake<IMultiplayerActorAccessor>();
            _hotkeysService = A.Fake<IHotkeysService>();

            _multiplayer = new Multiplayer(
                _logger,
                _uiFactory,
                _lobbyWindowController,
                _multiplerActorAccessor,
                _gameInteractionService,
                _hotkeysService,
                _idGenerator);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void IsActive_TestCase_DependsOnAccessor(bool actorStatus)
        {
            // Arrange
            var current = A.Fake<IMultiplayerActor>();
            A.CallTo(() => current.IsActive).Returns(actorStatus);
            A.CallTo(() => _multiplerActorAccessor.Current).Returns(current);

            // Act
            var actualStatus = _multiplayer.IsActive;

            // Assert
            Assert.That(actualStatus, Is.EqualTo(actorStatus));
        }

        [Test]
        public void Initialize_HostAndClientAreNotActive_CallsFactoryWithoutCallingDispose()
        {
            // Arrange
            A.CallTo(() => _multiplayerHost.IsActive).Returns(false);
            A.CallTo(() => _multiplayerClient.IsActive).Returns(false);

            // Act
            _multiplayer.Initialize();

            // Assert
            A.CallTo(() => _uiFactory.InitializeMultiplayerWindow()).MustHaveHappenedOnceExactly();
            A.CallTo(() => _multiplayerHost.Reset()).MustNotHaveHappened();
            A.CallTo(() => _multiplayerClient.Reset()).MustNotHaveHappened();
        }

        [Test]
        public void Initialize_MultiplayerHostIsActive_LogsAndCallsDispose()
        {
            // Arrange
            A.CallTo(() => _multiplerActorAccessor.Host).Returns(_multiplayerHost);
            A.CallTo(() => _multiplerActorAccessor.Client).Returns(_multiplayerClient);
            A.CallTo(() => _multiplayerHost.IsActive).Returns(true);

            // Act
            _multiplayer.Initialize();

            // Assert
            A.CallTo(() => _multiplayerHost.Reset()).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void TerminateMultiplayer_DisposesEverything()
        {
            // Arrange
            A.CallTo(() => _multiplerActorAccessor.Host).Returns(_multiplayerHost);
            A.CallTo(() => _multiplerActorAccessor.Client).Returns(_multiplayerClient);

            // Act
            _multiplayer.TerminateMultiplayer();

            // Assert
            A.CallTo(() => _multiplayerClient.Reset()).MustHaveHappenedOnceExactly();
            A.CallTo(() => _multiplayerHost.Reset()).MustHaveHappenedOnceExactly();
            A.CallTo(() => _lobbyWindowController.Reset()).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void Initialize_MultiplayerClientIsActive_LogsAndCallsDispose()
        {
            // Arrange
            A.CallTo(() => _multiplerActorAccessor.Host).Returns(_multiplayerHost);
            A.CallTo(() => _multiplerActorAccessor.Client).Returns(_multiplayerClient);
            A.CallTo(() => _multiplayerClient.IsActive).Returns(true);

            // Act
            _multiplayer.Initialize();

            // Assert
            A.CallTo(() => _multiplayerClient.Reset()).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void RealTimeDiceRoll_UsesLocalCalculationOutsideRealTimeOrDuringBarrier()
        {
            A.CallTo(() => _multiplerActorAccessor.Host).Returns(_multiplayerHost);
            A.CallTo(() => _multiplerActorAccessor.Client).Returns(_multiplayerClient);
            var calculations = 0;
            Assert.That(_multiplayer.ResolveRealTimeDiceRoll("rule", () => { calculations++; return 5; }), Is.EqualTo(5));

            A.CallTo(() => _multiplayerHost.IsRealTimeCombatActive).Returns(true);
            A.CallTo(() => _multiplayerHost.IsRealTimeCombatBarrierActive).Returns(true);
            Assert.That(_multiplayer.ResolveRealTimeDiceRoll("rule", () => { calculations++; return 6; }), Is.EqualTo(6));
            Assert.That(calculations, Is.EqualTo(2));
            A.CallTo(() => _multiplayerHost.ResolveRealTimeDiceRoll(A<string>._, A<Func<int>>._)).MustNotHaveHappened();
        }

        [Test]
        public void RealTimeDiceRoll_RoutesToActiveHostOrClient()
        {
            A.CallTo(() => _multiplerActorAccessor.Host).Returns(_multiplayerHost);
            A.CallTo(() => _multiplerActorAccessor.Client).Returns(_multiplayerClient);
            A.CallTo(() => _multiplayerHost.IsRealTimeCombatActive).Returns(true);
            A.CallTo(() => _multiplayerHost.ResolveRealTimeDiceRoll("rule", A<Func<int>>._)).Returns(17);
            Assert.That(_multiplayer.ResolveRealTimeDiceRoll("rule", () => 5), Is.EqualTo(17));

            A.CallTo(() => _multiplayerHost.IsRealTimeCombatActive).Returns(false);
            A.CallTo(() => _multiplayerClient.IsRealTimeCombatActive).Returns(true);
            A.CallTo(() => _multiplayerClient.ResolveRealTimeDiceRoll("rule", A<Func<int>>._)).Returns(19);
            Assert.That(_multiplayer.ResolveRealTimeDiceRoll("rule", () => 5), Is.EqualTo(19));
        }
    }
}
