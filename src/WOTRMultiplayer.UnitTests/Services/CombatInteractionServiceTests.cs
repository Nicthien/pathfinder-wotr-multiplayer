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
using WOTRMultiplayer.Services;
using Kingmaker.Visual.Animation.Kingmaker;
using Kingmaker.View.Animation;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Persistence.JsonUtility;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class CombatInteractionServiceTests
    {
        [Test]
        public void NativeRestoreReferenceExcludesHistoricalRealTimeParticipants()
        {
            var state = new NetworkCombatState
            {
                Units =
                [
                    new NetworkUnit { Id = "active-party" },
                    new NetworkUnit { Id = "active-enemy" },
                    new NetworkUnit { Id = "withdrawn-enemy" }
                ]
            };

            CombatInteractionService.RetainNativeCombatUnits(state,
                new[] { "ACTIVE-PARTY", "active-enemy" });

            Assert.That(state.Units.ConvertAll(unit => unit.Id),
                Is.EqualTo(new[] { "active-party", "active-enemy" }));
        }

        [TestCase(true, false, 12, false)]
        [TestCase(true, true, 0, true)]
        [TestCase(false, false, 0, false)]
        [TestCase(false, false, 1, true)]
        public void CombatInitializationUsesNativeMembershipInRealTimeMode(
            bool turnBasedEnabled,
            bool turnBasedInitialized,
            int nativeCombatUnits,
            bool expected)
        {
            Assert.That(CombatInteractionService.IsCombatInitializationReady(
                turnBasedEnabled,
                turnBasedInitialized,
                nativeCombatUnits), Is.EqualTo(expected));
        }

        [Test]
        public void NativeCheckpointPreparationSupportsContextlessSavedBuff()
        {
            var prepared = BuffInteractionService.PrepareNativeBuffForCheckpoint(
                new BlueprintBuff(), null, TimeSpan.MaxValue, "contextless-native-buff");

            Assert.That(prepared.Context, Is.Null);
            Assert.That(prepared.UniqueId, Is.EqualTo("contextless-native-buff"));
            Assert.That(prepared.IsPostLoadExecuted, Is.True);
            Assert.That(RealTimeBuffReplicaStore.IsReplica(prepared), Is.False);
        }

        [Test]
        public void NativeReconstructionRemovesOnlyTransactionIntroducedSurplus()
        {
            var existing = (Kingmaker.UnitLogic.Buffs.Buff)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                    typeof(Kingmaker.UnitLogic.Buffs.Buff));
            var introduced = (Kingmaker.UnitLogic.Buffs.Buff)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                    typeof(Kingmaker.UnitLogic.Buffs.Buff));
            var before = new System.Collections.Generic.HashSet<EntityFact> { existing };

            Assert.That(CombatInteractionService.TakeTransactionIntroducedSurplus(
                new[] { existing, introduced }, before, expectedCount: 1), Is.EqualTo(new[] { introduced }));
            Assert.That(CombatInteractionService.TakeTransactionIntroducedSurplus(
                new[] { existing, introduced }, before, expectedCount: 2), Is.Empty);
        }

        [Test]
        public void NativeReconstructionKeepsAnIntroducedFactWithTheHostId()
        {
            var expected = (Kingmaker.UnitLogic.Buffs.Buff)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                    typeof(Kingmaker.UnitLogic.Buffs.Buff));
            var sideEffect = (Kingmaker.UnitLogic.Buffs.Buff)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                    typeof(Kingmaker.UnitLogic.Buffs.Buff));
            var uniqueId = typeof(EntityFact).GetField("<UniqueId>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            uniqueId.SetValue(expected, "host-id");
            uniqueId.SetValue(sideEffect, "side-effect-id");

            Assert.That(CombatInteractionService.TakeTransactionIntroducedSurplus(
                new[] { expected, sideEffect }, new System.Collections.Generic.HashSet<EntityFact>(),
                expectedCount: 1, expectedIds: new System.Collections.Generic.HashSet<string> { "host-id" }),
                Is.EqualTo(new[] { sideEffect }));
        }

        [Test]
        public void NativeReconstructionFinalSweepRemovesLoadTimeSurplusButKeepsHostId()
        {
            var expected = (Kingmaker.UnitLogic.Buffs.Buff)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                    typeof(Kingmaker.UnitLogic.Buffs.Buff));
            var loadSideEffect = (Kingmaker.UnitLogic.Buffs.Buff)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                    typeof(Kingmaker.UnitLogic.Buffs.Buff));
            var uniqueId = typeof(EntityFact).GetField("<UniqueId>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            uniqueId.SetValue(expected, "host-id");
            uniqueId.SetValue(loadSideEffect, "load-side-effect-id");

            Assert.That(CombatInteractionService.TakeAuthoritativeSurplus(
                    new[] { expected, loadSideEffect }, expectedCount: 1,
                    expectedIds: new System.Collections.Generic.HashSet<string> { "host-id" }),
                Is.EqualTo(new[] { loadSideEffect }));
            Assert.That(CombatInteractionService.TakeAuthoritativeSurplus(
                new[] { expected, loadSideEffect }, expectedCount: 2), Is.Empty);
        }

        [Test]
        public void EmptyProjectedContextDoesNotRejectAReconstructedNativeContext()
        {
            var native = (Kingmaker.UnitLogic.Mechanics.MechanicsContext)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                    typeof(Kingmaker.UnitLogic.Mechanics.MechanicsContext));

            Assert.That(RealTimeBuffContextReplication.Validate(native,
                new System.Collections.Generic.List<NetworkBuffContextState>()), Is.Null);
            Assert.DoesNotThrow(() => RealTimeBuffContextReplication.Apply(native,
                new System.Collections.Generic.List<NetworkBuffContextState>(), _ => null));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeReferenceRevocationOrForeignReceiptNeverAccessesWorld(bool authorized)
        {
            var mainThread = A.Fake<IMainThreadAccessor>();
            A.CallTo(() => mainThread.Post(A<Action>._)).Invokes((Action action) => action());
            var lookup = A.Fake<IGameStateLookupService>();
            var service = new CombatInteractionService(A.Fake<ILogger<CombatInteractionService>>(), lookup,
                A.Fake<IPlayerNotificationService>(), A.Fake<IBuffInteractionService>(), mainThread, A.Fake<IMapper>());
            var task = service.TryRestoreNativeReferenceStateAsync(new object(), new object(), new NetworkCombatState(), () => authorized);
            if (authorized) Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
            else Assert.That(task.Result, Is.Null);
            A.CallTo(() => lookup.GetUnitEntity(A<string>._)).MustNotHaveHappened();
        }

        [Test]
        public void NativeReferenceCannotReplaceMissingMechanicalAreaWithPresentationReplica()
        {
            var lookup = A.Fake<IGameStateLookupService>();
            A.CallTo(() => lookup.GetAreaEffect(A<NetworkAreaEffect>._)).Returns(null);
            var buffs = A.Fake<IBuffInteractionService>();
            var service = new CombatInteractionService(A.Fake<ILogger<CombatInteractionService>>(), lookup,
                A.Fake<IPlayerNotificationService>(), buffs, A.Fake<IMainThreadAccessor>(), A.Fake<IMapper>());
            var reference = new NetworkCombatState
            { AreaEffects = new System.Collections.Generic.List<NetworkAreaEffect> { new() { Id = "missing-native-area" } } };
            var apply = typeof(CombatInteractionService).GetMethod("ApplyRealTimeCombatState",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            using var transaction = new RealTimeBuffRestoreTransaction();
            var error = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                apply.Invoke(service, new object[] { reference, transaction, true }));
            Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(error.InnerException.Message, Does.Contain("missing-area:missing-native-area"));
            A.CallTo(() => buffs.StageMissingUnitBuffsPresentation(A<Kingmaker.EntitySystem.Entities.UnitEntityData>._,
                A<NetworkUnitBuffCollection>._, A<RealTimeBuffRestoreTransaction>._)).MustNotHaveHappened();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConfirmedKineticistValueDoesNotNotifyMechanicsOnPresentationClient(bool presentationOnly)
        {
            var part = (Kingmaker.UnitLogic.Class.Kineticist.UnitPartKineticist)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Kingmaker.UnitLogic.Class.Kineticist.UnitPartKineticist));
            var holder = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                typeof(Kingmaker.UnitLogic.Class.Kineticist.UnitPartKineticistBurnHolder));
            typeof(Kingmaker.UnitLogic.Class.Kineticist.UnitPartKineticist).GetField("m_BurnHolderCache",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(part, holder);
            CombatInteractionService.ApplyConfirmedKineticistBurn(part, 1, true, _ => Assert.Fail("Unexpected native notification"));
            var calls = 0;
            Assert.That(CombatInteractionService.ApplyConfirmedKineticistBurn(part, 4, presentationOnly, previous =>
            { Assert.That(previous, Is.EqualTo(1)); calls++; }), Is.True);
            Assert.That(part.AcceptedBurn, Is.EqualTo(4));
            Assert.That(calls, Is.EqualTo(presentationOnly ? 0 : 1));
            Assert.That(CombatInteractionService.ApplyConfirmedKineticistBurn(part, 4, presentationOnly, _ => calls++), Is.False);
            Assert.That(calls, Is.EqualTo(presentationOnly ? 0 : 1));
        }

        [Test]
        public void StaleFinalizationNeverTouchesTheWorldOrRunsCleanup()
        {
            var mainThread = A.Fake<IMainThreadAccessor>();
            Action pending = null;
            A.CallTo(() => mainThread.Post(A<Action>._)).Invokes((Action action) => pending = action);
            var service = new CombatInteractionService(
                A.Fake<ILogger<CombatInteractionService>>(), A.Fake<IGameStateLookupService>(),
                A.Fake<IPlayerNotificationService>(), A.Fake<IBuffInteractionService>(), mainThread, A.Fake<IMapper>());
            var current = true;
            var cleaned = false;
            var task = service.TryFinalizeRealTimeCombatAsync(() => current, () => cleaned = true);
            current = false;
            pending();
            Assert.That(task.Result, Is.False);
            Assert.That(cleaned, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AbilityPresentation_StaleOrMalformed_DoesNotLookUpUnits(bool current)
        {
            var mainThread = A.Fake<IMainThreadAccessor>();
            A.CallTo(() => mainThread.Post(A<Action>._)).Invokes((Action action) => action());
            var lookup = A.Fake<IGameStateLookupService>();
            var service = new CombatInteractionService(
                A.Fake<ILogger<CombatInteractionService>>(), lookup,
                A.Fake<IPlayerNotificationService>(), A.Fake<IBuffInteractionService>(),
                mainThread, A.Fake<IMapper>());
            var task = service.TryPresentRealTimeActionAsync(
                new WOTRMultiplayer.Networking.Messages.Game.NotifyRealTimeAuthoritativeEvent
                {
                    UnitId = "hero", ActionId = "cast",
                    Kind = (int)WOTRMultiplayer.Networking.Messages.Game.RealTimeAuthoritativeEventKind.AbilityPresentation,
                    Ability = new WOTRMultiplayer.Networking.Messages.Game.RealTimeAbilityPresentationPayload
                        { Stage = 1, SpeedScale = 1, BlueprintId = "not-a-blueprint" }
                }, () => current);
            Assert.That(task.Result, Is.EqualTo(current ? (bool?)false : null));
            A.CallTo(() => lookup.GetUnitEntity(A<string>._)).MustNotHaveHappened();
        }

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
            var task = service.TryPresentRealTimeActionAsync(
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
        public void ValidActionWithUnavailableLocalRenderer_IsSafelyOmitted()
        {
            var mainThread = A.Fake<IMainThreadAccessor>();
            A.CallTo(() => mainThread.Post(A<Action>._)).Invokes((Action action) => action());
            var lookup = A.Fake<IGameStateLookupService>();
            A.CallTo(() => lookup.GetUnitEntity("unit-a")).Returns(null);
            var service = new CombatInteractionService(
                A.Fake<ILogger<CombatInteractionService>>(), lookup,
                A.Fake<IPlayerNotificationService>(), A.Fake<IBuffInteractionService>(),
                mainThread, A.Fake<IMapper>());

            var task = service.TryPresentRealTimeActionAsync(
                new WOTRMultiplayer.Networking.Messages.Game.NotifyRealTimeAuthoritativeEvent
                {
                    UnitId = "unit-a", ActionId = "attack-a",
                    Kind = (int)WOTRMultiplayer.Networking.Messages.Game.RealTimeAuthoritativeEventKind.AttackPresentation,
                    Attack = new WOTRMultiplayer.Networking.Messages.Game.RealTimeAttackPresentationPayload
                    {
                        Stage = (int)RealTimePresentationStage.Start,
                        AnimationType = (int)UnitAnimationType.MainHandAttack,
                        WeaponStyle = (int)WeaponAnimationStyle.None,
                        SpeedScale = 1,
                        TargetDistance = 1
                    }
                }, () => true);

            Assert.That(task.Result, Is.True);
            A.CallTo(() => lookup.GetUnitEntity("unit-a")).MustHaveHappenedOnceExactly();
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
                Is.EqualTo(RealTimePresentationRuntime.Enabled
                    ? "invalid-area:cloud:blueprint,invalid-area:cloud:position,invalid-area:cloud:reconstruction-context,invalid-area:cloud:reconstruction-target-or-scene,missing-unit:summon"
                    : "missing-area:cloud,missing-unit:summon"));
            A.CallTo(() => lookup.GetAreaEffects()).MustNotHaveHappened();
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
            Assert.That(task.Exception?.GetBaseException().Message, Is.EqualTo(RealTimePresentationRuntime.Enabled
                ? "invalid-area:missing-area:blueprint,invalid-area:missing-area:position,invalid-area:missing-area:reconstruction-context,invalid-area:missing-area:reconstruction-target-or-scene"
                : "missing-area:missing-area"));
            A.CallTo(() => lookup.GetAreaEffects()).MustNotHaveHappened();
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

        [Test]
        public void PresentationBuild_RejectsLegacyKillBeforeWorldLookup()
        {
            var mainThread = A.Fake<IMainThreadAccessor>();
            var lookup = A.Fake<IGameStateLookupService>();
            A.CallTo(() => mainThread.Post(A<Action>._)).Invokes((Action action) => action());
            var service = new CombatInteractionService(
                A.Fake<ILogger<CombatInteractionService>>(), lookup,
                A.Fake<IPlayerNotificationService>(), A.Fake<IBuffInteractionService>(),
                mainThread, A.Fake<IMapper>());
            var task = service.TryApplyRealTimeUnitDeathAsync("enemy", new NetworkPlayer(0),
                () => RealTimePresentationRuntime.Enabled);
            if (RealTimePresentationRuntime.Enabled) Assert.That(task.Result, Is.False);
            else Assert.That(task.Result, Is.Null);
            A.CallTo(() => lookup.GetUnitEntity(A<string>._)).MustNotHaveHappened();
        }
    }
}
