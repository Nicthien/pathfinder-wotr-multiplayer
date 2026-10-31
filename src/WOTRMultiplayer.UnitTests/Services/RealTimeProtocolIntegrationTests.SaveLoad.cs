using System;
using System.Collections.Generic;
using System.Reflection;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions.Settings;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Settings;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Networking.Messages.Lobby;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class RealTimeProtocolIntegrationTests
    {
        [Test]
        public void SaveLoadRevokesPresentationContextBeforeNativeHydration()
        {
            var (host, client, _, _, _, _) = CreatePair();
            foreach (var actor in new MultiplayerActorBase[] { host, client })
            {
                var old = actor.Game.RealTimeCombat;
                old.Mode = old.PreferredMode = CombatMode.RealTimeWithPause;
                old.Epoch = 12; old.PreferenceSequence = 9;
                old.PausedPlayers.Add(42); old.LastAuthoritativeSequence = 71;
                actor.Game.LastCombatTurn = new NetworkCombatTurn { UnitId = "old-turn" };
                actor.BeginSaveLoad();
                Assert.That(actor.Game.Combat, Is.Null);
                Assert.That(actor.Game.LastCombatTurn, Is.Null);
                Assert.That(actor.Game.RealTimeCombat, Is.Not.SameAs(old));
                Assert.That(actor.Game.RealTimeCombat.Epoch, Is.EqualTo(13));
                Assert.That(actor.Game.RealTimeCombat.PreferredMode, Is.EqualTo(CombatMode.RealTimeWithPause));
                Assert.That(actor.Game.RealTimeCombat.PreferenceSequence, Is.EqualTo(9));
                Assert.That(actor.Game.RealTimeCombat.PausedPlayers, Is.Empty);
                Assert.That(actor.Game.RealTimeCombat.LastAuthoritativeSequence, Is.Zero);
                Assert.That(actor.IsSaveLoadPending, Is.True);
                actor.CombatStarted(); actor.CombatEnded(); actor.CombatRoundStarted(4);
                Assert.That(actor.Game.Combat, Is.Null, "Unloading must not create or finalize an encounter.");
                actor.TickCombatStartup();
                Assert.That(actor.HasCombatStartupFailure, Is.False);
            }
            Assert.That(client.IsRealTimeCombatActive, Is.False, "Native stat setters must be available during hydration.");
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
            Assert.That(client.IsSavingBlockedByCombatSync, Is.True);
        }

        [Test]
        public void SaveInfoRevokesClientCombatBeforeTheSaveChunksArrive()
        {
            var (_, client, _, network, _, _) = CreatePair();
            client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            FakeUtils.GetNetworkReceiverHandler<NotifySaveGameInfoChanged>(network)(NetworkConstants.HostPlayerId,
                new NotifySaveGameInfoChanged { GameId = "replacement", LoadedSaveSeed = 84,
                    ExpectedChunks = 1, ContentSize = 1 });
            Assert.That(client.Game.Combat, Is.Null);
            Assert.That(client.IsRealTimeCombatActive, Is.False);
            Assert.That(client.IsSaveLoadPending, Is.True);
            Assert.That(client.IsSavingBlockedByCombatSync, Is.True);
        }

        [Test]
        public void SaveLoadDiscardsQueuedFinalCombatCapture()
        {
            var actions = new Queue<Action>();
            var (host, _, _, _, messages, _) = CreatePair(mainThreadQueue: actions);
            host.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            host.CombatEnded();
            host.PublishRealTimeCombatSnapshot();
            host.BeginSaveLoad();
            while (actions.Count > 0) actions.Dequeue()();
            host.PublishRealTimeCombatSnapshot();
            Assert.That(messages, Is.Empty);
            Assert.That(host.Game.Combat, Is.Null);
            Assert.That(host.Game.RealTimeCombat.CombatEndPending, Is.False);
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
        }

        [Test]
        public void SaveLoadRemainsBlockedUntilNativeLifecycleAndAreaBarrierComplete()
        {
            var (host, client, _, _, _, _) = CreatePair();
            foreach (var actor in new MultiplayerActorBase[] { host, client })
            {
                actor.Game.Stage = NetworkLobbyStage.Playing;
                actor.Game.StartUp.SavePath = "missing-test-save.zks";
                var interaction = (IGameInteractionService)typeof(MultiplayerActorBase)
                    .GetProperty("GameInteraction", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(actor);
                A.CallTo(() => interaction.QuickLoadGame(A<string>._)).Returns(actor.Game.Id);
                actor.BeginSaveLoad();
                var state = actor.Game.RealTimeCombat;
                actor.OnGameLoaded();
                Assert.That(actor.IsSaveLoadPending, Is.True, "A stale pre-load callback cannot release the transfer guard.");
                typeof(MultiplayerActorBase).GetMethod("LoadSavedGame", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(actor, null);
                Assert.That(actor.Game.RealTimeCombat, Is.SameAs(state), "The native entry must not reset the same transfer twice.");
                Assert.That(actor.HoldsWorldBeforeSaveLoad, Is.False, "Native hydration must be allowed to calculate stats.");
                actor.Game.ForcedPause = new NetworkForcedPause { Reason = NetworkForcedPauseReason.AreaLoading };
                actor.OnGameLoaded();
                Assert.That(actor.IsSaveLoadPending, Is.False);
            }
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
            Assert.That(client.IsSavingBlockedByCombatSync, Is.True);
            host.Game.ForcedPause = client.Game.ForcedPause = null;
            Assert.That(host.IsSavingBlockedByCombatSync, Is.False);
            Assert.That(client.IsSavingBlockedByCombatSync, Is.False);
        }

        [Test]
        public void NativeLoadEntryReleasesTheHostGuardAfterGameLoaded()
        {
            var (host, _, _, _, _, _) = CreatePair();
            var settings = (IMultiplayerSettingsService)typeof(MultiplayerActorBase)
                .GetProperty("SettingsService", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(host);
            A.CallTo(() => settings.GetSettings()).Returns(new NetworkMultiplayerSettings { SaveGameChunkSize = 32768 });
            host.ForceLoadGame(host.Game.Id, "missing-test-save.zks");
            Assert.That(host.IsSaveLoadPending, Is.True);
            Assert.That(host.HoldsWorldBeforeSaveLoad, Is.False);
            host.OnGameLoaded();
            Assert.That(host.IsSaveLoadPending, Is.False);
        }

        [Test]
        public void QueuedNativeLoadIsAdmittedOnceAndASecondLoadIsRefused()
        {
            var (_, client, _, _, _, _) = CreatePair();
            client.Game.Stage = NetworkLobbyStage.Playing;
            client.Game.StartUp.SavePath = "missing-test-save.zks";
            var interaction = (IGameInteractionService)typeof(MultiplayerActorBase)
                .GetProperty("GameInteraction", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
            A.CallTo(() => interaction.QuickLoadGame(A<string>._)).Returns(client.Game.Id);
            typeof(MultiplayerActorBase).GetMethod("LoadSavedGame", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(client, null);
            var seed = client.Game.LoadedSaveSeed;
            Assert.DoesNotThrow(() => client.ForceLoadGame(client.Game.Id, "missing-test-save.zks"));
            Assert.Throws<InvalidOperationException>(() => client.ForceLoadGame(client.Game.Id, "missing-test-save.zks"));
            Assert.That(client.Game.LoadedSaveSeed, Is.EqualTo(seed));
        }

        [Test]
        public void NativeLoadCannotProceedAcrossAnActiveCombatBarrier()
        {
            var (host, _, _, _, _, _) = CreatePair();
            host.Game.RealTimeCombat.Phase = CombatSynchronizationPhase.Repairing;
            var state = host.Game.RealTimeCombat;
            Assert.Throws<InvalidOperationException>(() => host.ForceLoadGame(host.Game.Id, "missing-test-save.zks"));
            Assert.That(host.Game.RealTimeCombat, Is.SameAs(state));
            Assert.That(host.IsSaveLoadPending, Is.False);
        }
    }
}
