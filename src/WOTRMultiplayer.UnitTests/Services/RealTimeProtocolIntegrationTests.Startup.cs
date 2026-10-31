using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class RealTimeProtocolIntegrationTests
    {
        [TestCase(NetworkCombatStage.Initiating)]
        [TestCase(NetworkCombatStage.Collecting)]
        [TestCase(NetworkCombatStage.Reconciling)]
        [TestCase(NetworkCombatStage.Resyncing)]
        [TestCase(NetworkCombatStage.Finalizing)]
        public void StartupBlocksSavingAndRetainsEncounterOnUnexpectedNativeEnd(NetworkCombatStage stage)
        {
            var (host, client, _, _, _, _) = CreatePair();
            foreach (var actor in new MultiplayerActorBase[] { host, client })
            {
                actor.Game.Combat.Stage = stage;
                var encounter = actor.Game.Combat;
                Assert.That(actor.IsCombatStartupBlocked, Is.True);
                Assert.That(actor.HoldsCombatMembership, Is.True);
                actor.CombatEnded();
                Assert.That(actor.Game.Combat, Is.SameAs(encounter));
                actor.Game.Combat = null; // The safety latch must survive lost membership.
                Assert.That(actor.IsCombatStartupBlocked, Is.True);
                actor.CombatStarted();
                Assert.That(actor.Game.Combat, Is.Null);
            }
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
            Assert.That(client.IsSavingBlockedByCombatSync, Is.True);
        }

        [Test]
        public void StartupTimeoutIsProtectedAndOnlyANewSaveClearsTheFailure()
        {
            var (host, client, _, _, _, _) = CreatePair();
            foreach (var actor in new MultiplayerActorBase[] { host, client })
            {
                actor.Game.Combat.Stage = NetworkCombatStage.Resyncing;
                actor.Game.Combat.StartedAt = DateTime.UtcNow.AddSeconds(-31);
                actor.TickCombatStartup();
                Assert.That(actor.HasCombatStartupFailure, Is.True);
                Assert.That(actor.ToggleManualPause(false), Is.False);
                Assert.That(actor.ToggleManualPause(true), Is.False);
                actor.Game.Combat.Stage = NetworkCombatStage.Running;
                actor.TickCombatStartup();
                Assert.That(actor.HasCombatStartupFailure, Is.True);
                actor.Game.LoadedSaveSeed++;
                Assert.That(actor.HasCombatStartupFailure, Is.False);
            }
        }

        [Test]
        public void FirstTurnMembershipIsHeldUntilInitializationAcknowledgements()
        {
            var (host, client, _, _, _, _) = CreatePair();
            foreach (var actor in new MultiplayerActorBase[] { host, client })
            {
                Assert.That(actor.HoldsCombatMembership, Is.True);
                actor.Game.Combat.Turn = new NetworkCombatTurn { Stage = NetworkCombatTurnStage.Starting };
                Assert.That(actor.HoldsCombatMembership, Is.True);
                actor.Game.Combat.Turn.Stage = NetworkCombatTurnStage.Initialization;
                Assert.That(actor.HoldsCombatMembership, Is.True);
                actor.Game.Combat.Turn.Stage = NetworkCombatTurnStage.Playing;
                Assert.That(actor.HoldsCombatMembership, Is.False);
            }
        }

        [Test]
        public void RealTimeStartupRemainsHeldUntilAnEpochIsCommitted()
        {
            var (host, client, _, _, _, _) = CreatePair();
            foreach (var actor in new MultiplayerActorBase[] { host, client })
            {
                actor.Game.Combat.RealTimeBootstrapPending = true;
                actor.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
                actor.TickCombatStartup();
                Assert.That(actor.IsCombatStartupBlocked, Is.True);
                actor.Game.RealTimeCombat.Epoch = 1;
                actor.Game.RealTimeCombat.Phase = CombatSynchronizationPhase.Synchronizing;
                actor.TickCombatStartup();
                Assert.That(actor.IsCombatStartupBlocked, Is.True);
                actor.Game.RealTimeCombat.Phase = CombatSynchronizationPhase.Stable;
                actor.TickCombatStartup();
                Assert.That(actor.IsCombatStartupBlocked, Is.False);
            }
        }

        [Test]
        public void NetworkTickAdvancesRealTimeFinalizationWithoutNativeTurnControllerTick()
        {
            var (host, _, _, _, queued, _) = CreatePair();
            var combatService = (ICombatInteractionService)typeof(MultiplayerActorBase)
                .GetProperty("CombatInteraction", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(host);
            A.CallTo(() => combatService.IsCombatInitialized()).Returns(true);
            A.CallTo(() => combatService.GetCombatState()).Returns(new NetworkCombatState());
            host.Game.Combat.Stage = NetworkCombatStage.Finalizing;
            host.TickCombatStartup();
            Assert.That(queued.Dequeue(), Is.TypeOf<NotifyCombatInitializationRequired>());
            host.Game.Combat.PlayersFinishedStartupSequence.TryAdd(42, true);
            host.TickCombatStartup();
            Assert.That(host.Game.Combat.Stage, Is.EqualTo(NetworkCombatStage.Running));
        }

        [Test]
        public void SafetyPauseBypassesNormalRequestGatesDuringStartup()
        {
            var (host, client, _, _, _, _) = CreatePair();
            host.Game.Combat.Stage = client.Game.Combat.Stage = NetworkCombatStage.Resyncing;
            Assert.That(host.RequestLocalRealTimePause(true), Is.True);
            Assert.That(client.RequestRealTimePause(true), Is.True);
            Assert.That(host.RequestLocalRealTimePause(false), Is.False);
            Assert.That(client.RequestRealTimePause(false), Is.False);
        }

        [Test]
        public void ManualSafetyPauseDoesNotAskTheKeyboardCallerToToggleAgain()
        {
            var (host, client, _, _, _, _) = CreatePair();
            foreach (var actor in new MultiplayerActorBase[] { host, client })
            {
                actor.Game.Combat.Stage = NetworkCombatStage.Resyncing;
                var gameService = (IGameInteractionService)typeof(MultiplayerActorBase)
                    .GetProperty("GameInteraction", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(actor);
                Assert.That(actor.ToggleManualPause(false), Is.False);
                Assert.That(gameService.IsPaused, Is.True);
                actor.ForceUnpause();
                Assert.That(gameService.IsPaused, Is.True);
            }
        }

        [Test]
        public async Task HostRestartTimeoutKeepsSavingBlockedWhenNativeCombatNeverReturns()
        {
            var (host, _, _, _, _, _) = CreatePair();
            var combatService = (ICombatInteractionService)typeof(MultiplayerActorBase)
                .GetProperty("CombatInteraction", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(host);
            A.CallTo(() => combatService.ForceResetCombatAsync()).Returns(Task.CompletedTask);
            var handler = typeof(MultiplayerHost).GetMethod("OnNotifyCombatInitiatedAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var pending = (Task)handler.Invoke(host, new object[] { 42L,
                new NotifyCombatInitiated { PlayerId = 42, InitialUnits = new List<string>() } });
            Assert.That(host.Game.Combat, Is.Null);
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True, "Restart must protect the gap before the timeout too.");
            await pending;
            Assert.That(host.Game.Combat, Is.Null);
            Assert.That(host.HasCombatStartupFailure, Is.True);
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
        }
    }
}
