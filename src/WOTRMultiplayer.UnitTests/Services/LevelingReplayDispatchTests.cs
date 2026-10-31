using System;
using System.Collections.Generic;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Abstractions.Unity;
using WOTRMultiplayer.Entities.Leveling;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class LevelingReplayDispatchTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void MissingMechanicalSelectionReportsFailureInsteadOfSilentlyCompleting(bool feature)
        {
            var queued = new Queue<Action>();
            var dispatcher = A.Fake<IMainThreadAccessor>();
            A.CallTo(() => dispatcher.Post(A<Action>._)).Invokes((Action action) => queued.Enqueue(action));
            var service = new LevelingInteractionService(A.Fake<ILogger<LevelingInteractionService>>(), dispatcher,
                A.Fake<IUIAccessor>(), A.Fake<IPlayerNotificationService>(), A.Fake<IGameStateLookupService>(), A.Fake<IUISyncCountersService>());
            var failures = 0;
            using (service.EnterReplayScope(() => true, _ => failures++))
            {
                if (feature) service.SelectLevelingFeature(new NetworkLevelingFeature { Id = "missing", Name = "Missing" });
                else service.SelectLevelingClass(new NetworkLevelingClass { Id = "missing", Name = "Missing" });
            }
            Assert.Throws<InvalidOperationException>(() => queued.Dequeue()());
            Assert.That(failures, Is.EqualTo(1));
        }

        [Test]
        public void InnerDispatchReportsFailureEvenWhenApplicationInvalidatedItsGuard()
        {
            var queued = new Queue<Action>();
            var dispatcher = A.Fake<IMainThreadAccessor>();
            A.CallTo(() => dispatcher.Post(A<Action>._)).Invokes((Action action) => queued.Enqueue(action));
            var ui = A.Fake<IUIAccessor>();
            A.CallTo(() => ui.CharGenView).Throws(new InvalidOperationException("partial-application"));
            var service = new LevelingInteractionService(A.Fake<ILogger<LevelingInteractionService>>(), dispatcher,
                ui, A.Fake<IPlayerNotificationService>(), A.Fake<IGameStateLookupService>(), A.Fake<IUISyncCountersService>());
            var valid = true;
            var failures = 0;
            using (service.EnterReplayScope(() => { var accepted = valid; valid = false; return accepted; }, _ => failures++))
                service.SetLevelingName("old-name");
            Assert.Throws<InvalidOperationException>(() => queued.Dequeue()());
            Assert.That(failures, Is.EqualTo(1));
        }

        [TestCase("start")]
        [TestCase("complete")]
        [TestCase("name")]
        public void InnerDispatchRetainsAuthorizationAfterOuterScopeEnds(string operation)
        {
            var queued = new Queue<Action>();
            var dispatcher = A.Fake<IMainThreadAccessor>();
            A.CallTo(() => dispatcher.Post(A<Action>._)).Invokes((Action action) => queued.Enqueue(action));
            var lookup = A.Fake<IGameStateLookupService>();
            var service = new LevelingInteractionService(A.Fake<ILogger<LevelingInteractionService>>(),
                dispatcher, A.Fake<IUIAccessor>(), A.Fake<IPlayerNotificationService>(), lookup,
                A.Fake<IUISyncCountersService>());
            var current = true;
            using (service.EnterReplayScope(() => current))
            {
                if (operation == "start") service.StartBackgroundLeveling("old-unit", NetworkLevelingType.Leveling);
                else if (operation == "complete") service.CompleteLeveling();
                else service.SetLevelingName("old-name");
            }
            Assert.That(queued.Count, Is.EqualTo(1));
            current = false;
            Assert.DoesNotThrow(() => queued.Dequeue()());
            A.CallTo(() => lookup.GetUnitEntity(A<string>._)).MustNotHaveHappened();
        }
    }
}
