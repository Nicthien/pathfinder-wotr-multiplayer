using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using FakeItEasy;
using Kingmaker.EntitySystem.Persistence;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.GameModes;
using Kingmaker.UI;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions;
using WOTRMultiplayer.HarmonyPatches.Combat;
using WOTRMultiplayer.HarmonyPatches.GameInstance;
using WOTRMultiplayer.Services.GameInteraction;
using WOTRMultiplayer.Services.PubSub;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [NonParallelizable]
    public class RealTimeNativeClientLoadTests
    {
        private const string AreaId = "22222222222222222222222222222222";
        private static SaveInfo Info()
        {
            var source = new RealTimeMemorySaver();
            source.SaveJson("header", "{\"GameId\":\"campaign\"}");
            source.SaveJson("player", "{}"); source.SaveJson("party", "{}");
            source.SaveJson(AreaId, "{}"); source.SaveBytes("history", new byte[] { 1, 2, 3 }); source.Seal();
            return new SaveInfo { GameId = "campaign", FolderName = "RTwP-volatile-11111111111111111111111111111111.memory", Saver = source };
        }

        [TearDown]
        public void NoLeakedNativeOwnership() => Assert.That(RealTimeNativeLoadLease.IsActive, Is.False);

        [Test]
        public void ExactAreaEntryAdmittedOnceAndUserLoadBlockedWithoutTouchingGame()
        {
            var owner = new object(); var info = Info();
            using var lease = RealTimeNativeLoadLease.Begin(owner, info, () => true, (_, _) => { });
            Assert.That(GamePatches.Game_LoadGame_Prefix(null), Is.False);
            Assert.That(GamePatches.Game_LoadArea_Prefix(null), Is.False);
            using var other = Info().Saver;
            Assert.That(RealTimeNativeLoadLease.AdmitAreaLoad(new SaveInfo { Saver = other, FolderName = info.FolderName }), Is.False);
            Assert.That(GamePatches.Game_LoadArea_Prefix(info), Is.True);
            Assert.That(GamePatches.Game_LoadArea_Prefix(info), Is.False);
            lease.MarkNativeCallback();
            Assert.That(lease.NativeCallbackCompleted, Is.True);
        }

        [Test]
        public void SecondOwnerCannotReplaceFirstLease()
        {
            var info = Info(); var other = Info();
            using var lease = RealTimeNativeLoadLease.Begin(new object(), info, () => true, (_, _) => { });
            using (other.Saver)
                Assert.Throws<InvalidOperationException>(() => RealTimeNativeLoadLease.Begin(new object(), other, () => true, (_, _) => { }));
            Assert.That(RealTimeNativeLoadLease.AdmitAreaLoad(info), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeLeaseRequiresAuthorizationAndCannotOverwriteStashLock(bool lockFile)
        {
            var info = Info();
            if (lockFile)
            {
                using var mutable = RealTimeMemorySaver.CopySource(info.Saver);
                mutable.SaveBytes("lock", new byte[] { 1 }); mutable.Seal();
                info.Saver.Dispose(); info.Saver = mutable.Clone();
            }
            using (info.Saver)
            {
                if (lockFile) Assert.Throws<InvalidDataException>(() => RealTimeNativeLoadLease.Begin(new object(), info, () => true, (_, _) => { }));
                else Assert.Throws<InvalidOperationException>(() => RealTimeNativeLoadLease.Begin(new object(), info, () => false, (_, _) => { }));
            }
        }

        [Test]
        public void StashCapabilityFlowsToNativeClonesButNeverToOriginalOrForeignSources()
        {
            var info = Info(); var original = (RealTimeMemorySaver)info.Saver;
            var writes = new List<byte[]>();
            using var lease = RealTimeNativeLoadLease.Begin(new object(), info, () => true, (_, bytes) => { writes.Add(bytes); bytes[0] = 99; });
            Assert.Throws<ObjectDisposedException>(() => original.CopyToStash("history"));
            using var clone = info.Saver.Clone();
            Assert.Throws<InvalidOperationException>(() => clone.CopyToStash("history")); // Not admitted yet.
            Assert.That(RealTimeNativeLoadLease.AdmitAreaLoad(info), Is.True);
            clone.CopyToStash("history");
            Assert.That(writes.Count, Is.EqualTo(1));
            Assert.That(clone.ReadBytes("history"), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.Throws<InvalidOperationException>(() => clone.Save());
            using var foreign = Info().Saver;
            Assert.Throws<InvalidOperationException>(() => foreign.CopyToStash("history"));
            lease.Dispose();
            Assert.Throws<InvalidOperationException>(() => clone.CopyToStash("history"));
        }

        [Test]
        public void RevocationKeepsLifecycleSuppressedUntilNativeWorkDrainsButRevokesWritesAndHydration()
        {
            var owner = new object(); var authorized = true; var info = Info();
            using var lease = RealTimeNativeLoadLease.Begin(owner, info, () => authorized, (_, _) => Assert.Fail("No write authorized"));
            RealTimeNativeLoadLease.AdmitAreaLoad(info);
            using var clone = info.Saver.Clone();
            authorized = false;
            Assert.That(RealTimeNativeLoadLease.SuppressesLifecycle(owner), Is.True);
            Assert.That(RealTimePresentationRuntime.IsClientActive, Is.EqualTo(RealTimePresentationRuntime.Enabled));
            Assert.That(RealTimeNativeLoadLease.SuppressesLifecycle(new object()), Is.False);
            Assert.That(RealTimeNativeLoadLease.EnterHydration(info), Is.Null);
            Assert.Throws<InvalidOperationException>(() => clone.CopyToStash("history"));
            lease.MarkNativeCallback();
            Assert.That(lease.NativeCallbackCompleted, Is.False);
            lease.Dispose();
            Assert.That(RealTimeNativeLoadLease.SuppressesLifecycle(owner), Is.False);
        }

        [Test]
        public void FaultIsStickyAndCannotBeMistakenForNativeCompletion()
        {
            var info = Info();
            using var lease = RealTimeNativeLoadLease.Begin(new object(), info, () => true, (_, _) => Assert.Fail("Fault revoked write"));
            RealTimeNativeLoadLease.AdmitAreaLoad(info);
            Assert.That(RealTimeNativeLoadLease.RecordFault("blocked-rule"), Is.True);
            RealTimeNativeLoadLease.RecordFault("later-fault"); lease.MarkNativeCallback();
            Assert.That(lease.Fault, Is.EqualTo("blocked-rule"));
            Assert.That(lease.NativeCallbackCompleted, Is.False);
            Assert.That(RealTimeNativeLoadLease.EnterHydration(info), Is.Null);
            Assert.Throws<InvalidOperationException>(() => info.Saver.CopyToStash("history"));
        }

        [Test]
        public void NativeLoadingExceptionIsRetainedWithoutSwallowingOrInvokingGame()
        {
            using var lease = RealTimeNativeLoadLease.Begin(new object(), Info(), () => true, (_, _) => { });
            RealTimeNativeLoadFailurePatches.AfterTick(new InvalidOperationException("synthetic-loader-failure"));
            Assert.That(lease.Fault, Is.EqualTo("native-loading-process-failed:synthetic-loader-failure"));
            Assert.That(lease.NativeCallbackCompleted, Is.False);
        }

        [Test]
        public async Task HydrationFlowsOnlyToNativeChildTasksAndIsRevokedAfterDisposal()
        {
            var info = Info();
            using var lease = RealTimeNativeLoadLease.Begin(new object(), info, () => true, (_, _) => { });
            Assert.That(RealTimeNativeLoadLease.EnterHydration(info), Is.Null);
            RealTimeNativeLoadLease.AdmitAreaLoad(info);
            using var foreign = Info().Saver;
            Assert.That(RealTimeNativeLoadLease.EnterHydration(new SaveInfo { Saver = foreign, FolderName = info.FolderName }), Is.Null);
            var clonedInfo = new SaveInfo { Saver = info.Saver.Clone(), FolderName = info.FolderName };
            using (clonedInfo.Saver)
            {
                RealTimeNativeThreadedLoadPatches.BeforeLoad(clonedInfo, out var scope);
                Assert.That(scope, Is.Not.Null);
                Assert.That(await Task.Run(() => RealTimeNativeLoadLease.IsHydrating), Is.True);
                RealTimeNativeThreadedLoadPatches.AfterLoad(scope, null);
                Assert.That(RealTimeNativeLoadLease.IsHydrating, Is.False);
            }
            using (RealTimeNativeLoadLease.EnterHydration(info))
            {
                lease.Dispose();
                Assert.That(RealTimeNativeLoadLease.IsHydrating, Is.False);
                Assert.That(await Task.Run(() => RealTimeNativeLoadLease.IsHydrating), Is.False);
            }
        }

        [Test]
        public void HydrationRestoresFieldsButNeverPermitsCalculatedStatCallbacks()
        {
            var info = Info();
            using var lease = RealTimeNativeLoadLease.Begin(new object(), info, () => true, (_, _) => { });
            RealTimeNativeLoadLease.AdmitAreaLoad(info);
            using (RealTimeNativeLoadLease.EnterHydration(info))
            {
                var stat = (ModifiableValue)FormatterServices.GetUninitializedObject(typeof(ModifiableValue));
                Assert.That(RealTimeUnitResultPatches.ModifiableValue_set_BaseValue_Prefix(stat), Is.True);
                Assert.That(RealTimeUnitResultPatches.UnitDescriptor_set_Damage_Prefix(null), Is.True);
                Assert.That(RealTimeUnitResultPatches.ModifiableValue_UpdateValue_Prefix(stat), Is.False);
            }
            Assert.That(lease.Fault, Is.Null);
        }

        [Test]
        public void OnlyTransferredAreasCanEnterHydrationAndFailureRestoresScope()
        {
            var info = Info();
            using var lease = RealTimeNativeLoadLease.Begin(new object(), info, () => true, (_, _) => { });
            RealTimeNativeLoadLease.AdmitAreaLoad(info);
            Assert.That(RealTimeNativeLoadLease.EnterAreaHydration(new string('3', 32)), Is.Null);
            using (RealTimeNativeLoadLease.EnterAreaHydration(AreaId)) Assert.That(RealTimeNativeLoadLease.IsHydrating, Is.True);
            Assert.That(RealTimeNativeLoadLease.IsHydrating, Is.False);
            RealTimeNativeThreadedLoadPatches.BeforeLoad(info, out var scope);
            RealTimeNativeThreadedLoadPatches.AfterLoad(scope, new InvalidOperationException("synthetic"));
            Assert.That(RealTimeNativeLoadLease.IsHydrating, Is.False);
            Assert.That(lease.Fault, Does.StartWith("native-threaded-load-failed:"));
        }

        [Test]
        public void NativeLifecycleCannotResetNetworkCombatOwnershipOrArea()
        {
            var actor = A.Fake<IMultiplayerActor>(); var actors = A.Fake<IMultiplayerActorAccessor>();
            A.CallTo(() => actors.Current).Returns(actor);
            using var lease = RealTimeNativeLoadLease.Begin(actor, Info(), () => true, (_, _) => { });
            var subscriber = new MultiplayerSubscriber(A.Fake<Microsoft.Extensions.Logging.ILogger<MultiplayerSubscriber>>(), actors, A.Fake<AutoMapper.IMapper>());
            var combat = new MultiplayerCombatSubscriber(A.Fake<Microsoft.Extensions.Logging.ILogger<MultiplayerCombatSubscriber>>(), actors, A.Fake<AutoMapper.IMapper>());
            subscriber.OnAreaLoadingComplete(); subscriber.HandlePartyChanged(); subscriber.HandleWarning(WarningNotificationType.GameLoaded);
            combat.HandleRoundStarted(1); combat.HandlePartyCombatStateChanged(true); combat.HandlePartyCombatStateChanged(false);
            A.CallTo(() => actor.OnAreaLoadingComplete()).MustNotHaveHappened();
            A.CallTo(() => actor.UpdateCharactersOwnership()).MustNotHaveHappened();
            A.CallTo(() => actor.OnGameLoaded()).MustNotHaveHappened();
            A.CallTo(() => actor.CombatStarted()).MustNotHaveHappened();
            A.CallTo(() => actor.CombatEnded()).MustNotHaveHappened();
            A.CallTo(() => actor.CombatRoundStarted(A<int>._)).MustNotHaveHappened();
        }

        [Test]
        public void SavingRemainsBlockedAndNativeModeSwitchesDoNotRequireNetworkNotifications()
        {
            using var lease = RealTimeNativeLoadLease.Begin(new object(), Info(), () => true, (_, _) => { });
            var canSave = true;
            SaveManagerRealTimePatches.SaveManager_IsSaveAllowed_Postfix(ref canSave);
            Assert.That(canSave, Is.False);
            Assert.That(GamePatches.Game_StartMode_Prefix(GameModeType.Pause), Is.True);
            Assert.That(GamePatches.Game_StopMode_Prefix(GameModeType.Pause), Is.True);
            var world = new object(); RealTimeNativeGraphRepair.Add(world, lease); lease.Dispose();
            Assert.That(RealTimeNativeGraphRepair.RequiresRebuild(world), Is.True);
        }

        [TestCase("Saved Games")]
        [TestCase("Areas_43")]
        [TestCase("Areas_42/child")]
        public void NativeClearCannotBeAuthorizedForAnotherDirectory(string name)
        {
            var persistent = Path.Combine(TestContext.CurrentContext.WorkDirectory, "native-stash-paths");
            Assert.Throws<InvalidDataException>(() => RealTimeNativeStashPaths.ValidateRoot(Path.Combine(persistent, name), persistent, 42));
        }

        [TestCase("../player.json")]
        [TestCase("lock")]
        [TestCase("LOCK")]
        [TestCase("C:\\save.json")]
        public void StashTargetRejectsEscapesAndNativeLock(string name)
            => Assert.Throws<InvalidDataException>(() => RealTimeNativeStashPaths.Target(TestContext.CurrentContext.WorkDirectory, name));

        [Test]
        public void ExactSyntheticStashAcceptedButRecursiveContentRefused()
        {
            var temporary = Path.Combine(TestContext.CurrentContext.WorkDirectory, "native-stash-" + Guid.NewGuid().ToString("N"));
            var root = Path.Combine(temporary, "Areas_42"); Directory.CreateDirectory(root);
            try
            {
                Assert.That(RealTimeNativeStashPaths.ValidateRoot(root, temporary, 42), Is.EqualTo(root));
                Assert.That(RealTimeNativeStashPaths.Target(root, "history"), Is.EqualTo(Path.Combine(root, "history")));
                Directory.CreateDirectory(Path.Combine(root, "nested"));
                Assert.Throws<InvalidDataException>(() => RealTimeNativeStashPaths.ValidateRoot(root, temporary, 42));
            }
            finally { Directory.Delete(temporary, recursive: true); }
        }
    }
}
