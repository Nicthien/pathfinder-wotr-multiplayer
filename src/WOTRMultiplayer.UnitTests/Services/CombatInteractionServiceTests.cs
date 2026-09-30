using System;
using AutoMapper;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Abstractions.Unity;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class CombatInteractionServiceTests
    {
        [Test]
        public void UpdateRealTimeCombatStateAsync_MissingEntities_FaultsBeforePartialUpdate()
        {
            var mainThread = A.Fake<IMainThreadAccessor>();
            A.CallTo(() => mainThread.Post(A<Action>._)).Invokes((Action action) => action());
            var lookup = A.Fake<IGameStateLookupService>();
            A.CallTo(() => lookup.GetAreaEffect(A<NetworkAreaEffect>._)).Returns(null);
            var service = new CombatInteractionService(
                A.Fake<ILogger<CombatInteractionService>>(),
                lookup,
                A.Fake<IPlayerNotificationService>(),
                A.Fake<IBuffInteractionService>(),
                mainThread,
                A.Fake<IMapper>());

            var task = service.UpdateRealTimeCombatStateAsync(new NetworkCombatState
            {
                Units = [new NetworkUnit { Id = "summon" }],
                AreaEffects = [new NetworkAreaEffect { Id = "cloud" }]
            });

            Assert.That(task.IsFaulted, Is.True);
            Assert.That(task.Exception?.GetBaseException().Message,
                Is.EqualTo("missing-area:cloud,missing-unit:summon"));
        }

        [Test]
        public void UpdateCombatStateAsync_MainThreadFailure_CompletesFaultedInsteadOfHanging()
        {
            var mainThread = A.Fake<IMainThreadAccessor>();
            A.CallTo(() => mainThread.Post(A<Action>._)).Invokes((Action action) => action());
            var service = new CombatInteractionService(
                A.Fake<ILogger<CombatInteractionService>>(),
                A.Fake<IGameStateLookupService>(),
                A.Fake<IPlayerNotificationService>(),
                A.Fake<IBuffInteractionService>(),
                mainThread,
                A.Fake<IMapper>());

            var task = service.UpdateCombatStateAsync(null, null, requiresFullUpdate: false);

            Assert.That(task.IsCompleted, Is.True);
            Assert.That(task.IsFaulted, Is.True);
        }

        [Test]
        public void GetRealTimeCombatStateAsync_MainThreadFailure_CompletesFaultedInsteadOfHanging()
        {
            var mainThread = A.Fake<IMainThreadAccessor>();
            A.CallTo(() => mainThread.Post(A<Action>._)).Invokes((Action action) => action());
            var service = new CombatInteractionService(
                A.Fake<ILogger<CombatInteractionService>>(),
                A.Fake<IGameStateLookupService>(),
                A.Fake<IPlayerNotificationService>(),
                A.Fake<IBuffInteractionService>(),
                mainThread,
                A.Fake<IMapper>());

            var task = service.GetRealTimeCombatStateAsync();

            Assert.That(task.IsCompleted, Is.True);
            Assert.That(task.IsFaulted, Is.True);
        }
    }
}
