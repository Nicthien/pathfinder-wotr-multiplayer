using System;
using AutoMapper;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Abstractions.Unity;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class CombatInteractionServiceTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void AttackPresentation_StaleOrMalformed_DoesNotLookUpUnits(bool current)
        {
            var mainThread = A.Fake<IMainThreadAccessor>();
            A.CallTo(() => mainThread.Post(A<Action>._)).Invokes((Action action) => action());
            var lookup = A.Fake<IGameStateLookupService>();
            var service = new CombatInteractionService(
                A.Fake<ILogger<CombatInteractionService>>(), lookup,
                A.Fake<IPlayerNotificationService>(), A.Fake<IBuffInteractionService>(),
                mainThread, A.Fake<IMapper>());
            var task = service.TryPresentRealTimeAttackAsync(
                new WOTRMultiplayer.Networking.Messages.Game.NotifyRealTimeAuthoritativeEvent
                {
                    UnitId = "unit-a",
                    Kind = (int)WOTRMultiplayer.Networking.Messages.Game.RealTimeAuthoritativeEventKind.AttackPresentation,
                    Attack = new WOTRMultiplayer.Networking.Messages.Game.RealTimeAttackPresentationPayload
                        { SpeedScale = float.NaN }
                }, () => current);
            Assert.That(task.Result, Is.EqualTo(current ? (bool?)false : null));
            A.CallTo(() => lookup.GetUnitEntity(A<string>._)).MustNotHaveHappened();
        }

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

        [Test]
        public void ApplyAndReadRealTimeCombatStateAsync_MissingEntityFaultsInOneMainThreadDispatch()
        {
            var mainThread = A.Fake<IMainThreadAccessor>();
            var dispatches = 0;
            A.CallTo(() => mainThread.Post(A<Action>._)).Invokes((Action action) =>
            {
                dispatches++;
                action();
            });
            var lookup = A.Fake<IGameStateLookupService>();
            A.CallTo(() => lookup.GetAreaEffect(A<NetworkAreaEffect>._)).Returns(null);
            var service = new CombatInteractionService(
                A.Fake<ILogger<CombatInteractionService>>(), lookup,
                A.Fake<IPlayerNotificationService>(), A.Fake<IBuffInteractionService>(),
                mainThread, A.Fake<IMapper>());

            var task = service.ApplyAndReadRealTimeCombatStateAsync(new NetworkCombatState
            {
                AreaEffects = [new NetworkAreaEffect { Id = "missing-area" }]
            });

            Assert.That(dispatches, Is.EqualTo(1));
            Assert.That(task.IsFaulted, Is.True);
            Assert.That(task.Exception?.GetBaseException().Message, Is.EqualTo("missing-area:missing-area"));
        }

        [Test]
        public void TryApplyAndReadRealTimeCombatStateAsync_StaleQueuedCheckpointSkipsWorldMutation()
        {
            var mainThread = A.Fake<IMainThreadAccessor>();
            Action queued = null;
            A.CallTo(() => mainThread.Post(A<Action>._)).Invokes((Action action) => queued = action);
            var lookup = A.Fake<IGameStateLookupService>();
            var service = new CombatInteractionService(
                A.Fake<ILogger<CombatInteractionService>>(), lookup,
                A.Fake<IPlayerNotificationService>(), A.Fake<IBuffInteractionService>(),
                mainThread, A.Fake<IMapper>());
            var current = true;

            var task = service.TryApplyAndReadRealTimeCombatStateAsync(
                new NetworkCombatState { Units = [new NetworkUnit { Id = "old-unit" }] },
                () => current);
            Assert.That(queued, Is.Not.Null);
            Assert.That(task.IsCompleted, Is.False);
            current = false;
            queued();

            Assert.That(task.IsCompleted, Is.True);
            Assert.That(task.IsFaulted, Is.False);
            Assert.That(task.Result, Is.Null);
            A.CallTo(() => lookup.GetUnitEntity(A<string>._)).MustNotHaveHappened();
        }

        [Test]
        public void TryExecuteRealTimeCombatCommandAsync_StaleQueuedOrderSkipsGameExecution()
        {
            var mainThread = A.Fake<IMainThreadAccessor>();
            Action queued = null;
            A.CallTo(() => mainThread.Post(A<Action>._)).Invokes((Action action) => queued = action);
            var lookup = A.Fake<IGameStateLookupService>();
            var service = new CombatInteractionService(
                A.Fake<ILogger<CombatInteractionService>>(), lookup,
                A.Fake<IPlayerNotificationService>(), A.Fake<IBuffInteractionService>(),
                mainThread, A.Fake<IMapper>());
            var current = true;

            var task = service.TryExecuteRealTimeCombatCommandAsync(RealTimeCommandKind.Move,
                new NetworkUnitMoveTo { InitiatorUnitId = "old-unit", Destination = new NetworkVector3() },
                null, null, () => current);
            Assert.That(queued, Is.Not.Null);
            current = false;
            queued();

            Assert.That(task.IsFaulted, Is.False);
            Assert.That(task.Result, Is.Null);
            A.CallTo(() => lookup.GetUnitEntity(A<string>._)).MustNotHaveHappened();
        }

        [Test]
        public void TryPauseAtProjectileSafePointAsync_StaleSessionDoesNotPauseGame()
        {
            var mainThread = A.Fake<IMainThreadAccessor>();
            Action queued = null;
            A.CallTo(() => mainThread.Post(A<Action>._)).Invokes((Action action) => queued = action);
            var service = new CombatInteractionService(
                A.Fake<ILogger<CombatInteractionService>>(), A.Fake<IGameStateLookupService>(),
                A.Fake<IPlayerNotificationService>(), A.Fake<IBuffInteractionService>(),
                mainThread, A.Fake<IMapper>());
            var current = true;
            var pauseCalls = 0;

            var task = service.TryPauseAtProjectileSafePointAsync(() => current, () => pauseCalls++);
            current = false;
            queued();

            Assert.That(task.Result, Is.False);
            Assert.That(pauseCalls, Is.Zero);
        }

        [Test]
        public void TryApplyRealTimeUnitDeathAsync_StaleSessionDoesNotTouchWorld()
        {
            var mainThread = A.Fake<IMainThreadAccessor>();
            var lookup = A.Fake<IGameStateLookupService>();
            Action queued = null;
            A.CallTo(() => mainThread.Post(A<Action>._)).Invokes((Action action) => queued = action);
            var service = new CombatInteractionService(
                A.Fake<ILogger<CombatInteractionService>>(), lookup,
                A.Fake<IPlayerNotificationService>(), A.Fake<IBuffInteractionService>(),
                mainThread, A.Fake<IMapper>());
            var current = true;

            var task = service.TryApplyRealTimeUnitDeathAsync("enemy", new NetworkPlayer(0), () => current);
            current = false;
            queued();

            Assert.That(task.Result, Is.Null);
            A.CallTo(() => lookup.GetUnitEntity(A<string>._)).MustNotHaveHappened();
        }
    }
}
