using System;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Abstractions.Unity;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class GameInteractionServiceRealTimeTests
    {
        [Test]
        public void CombatModeChange_MainThreadFailure_CompletesTaskInsteadOfHanging()
        {
            var mainThread = A.Fake<IMainThreadAccessor>();
            A.CallTo(() => mainThread.Post(A<Action>._)).Invokes((Action action) => action());
            var service = new GameInteractionService(
                A.Fake<ILogger<GameInteractionService>>(),
                A.Fake<IUIAccessor>(),
                mainThread,
                A.Fake<IEquipmentDefinitions>(),
                A.Fake<IPlayerNotificationService>(),
                A.Fake<IUISyncCountersService>(),
                A.Fake<IGameStateLookupService>());

            var task = service.SetTurnBasedCombatModeAsync(false);

            Assert.That(task.IsCompleted, Is.True);
        }
    }
}
