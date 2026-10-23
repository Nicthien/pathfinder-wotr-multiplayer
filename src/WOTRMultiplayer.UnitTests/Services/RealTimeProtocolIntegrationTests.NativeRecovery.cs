using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Abstractions;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class RealTimeProtocolIntegrationTests
    {
        [Test]
        public async Task NativeRecovery_ResumeWaitsForCombatRosterRejoin()
        {
            if (!RealTimePresentationRuntime.Enabled) return;
            var reference = new NetworkCombatState
            {
                Units =
                [
                    new NetworkUnit { Id = "hero" }
                ]
            };
            var (host, client, hostNetwork, clientNetwork, queued, clientCombat) =
                CreatePair(endState: reference, nativeRecovery: true);
            var rosterJoined = false;
            A.CallTo(() => clientCombat.TryApplyAndReadRealTimeCombatStateAsync(
                    A<NetworkCombatState>._, A<Func<bool>>._, false))
                .ReturnsLazily((NetworkCombatState state, Func<bool> guard, bool _) =>
                    Task.FromResult(rosterJoined && guard() ? state : null));
            var rosterStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var rosterReady = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            A.CallTo(() => clientCombat.EnsureUnitsInCombatAsync(A<List<string>>._))
                .Invokes(() => rosterStarted.TrySetResult(true))
                .Returns(rosterReady.Task);

            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);
            await host.BeginRealTimeRepairAsync();
            var rosterDeadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < rosterDeadline && !rosterStarted.Task.IsCompleted)
            {
                Pump(queued, hostNetwork, clientNetwork);
                await Task.Delay(10);
            }

            Assert.That(rosterStarted.Task.IsCompleted, Is.True,
                $"host={host.Game.RealTimeCombat.RecoveryStatus}/{host.Game.RealTimeCombat.RecoveryFailure};"
                + $"client={client.Game.RealTimeCombat.RecoveryStatus}/{client.Game.RealTimeCombat.RecoveryFailure};"
                + $"phase={host.Game.RealTimeCombat.Phase}/{client.Game.RealTimeCombat.Phase};"
                + $"detail={host.LastNativeRecoveryFailure};transfer={host.LastNativeTransferFailure}");
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(host.IsSavingBlockedByCombatSync && client.IsSavingBlockedByCombatSync, Is.True);
            A.CallTo(() => clientCombat.EnsureUnitsInCombatAsync(
                A<List<string>>.That.Matches(ids => ids.SequenceEqual(new[] { "hero" }))))
                .MustHaveHappenedOnceExactly();

            rosterJoined = true;
            rosterReady.SetResult(new List<string>());
            await PumpRecovery(queued, hostNetwork, clientNetwork, host, client);
            AssertRecovered(host, client, clientCombat);
            A.CallTo(() => clientCombat.TryApplyAndReadRealTimeCombatStateAsync(
                    A<NetworkCombatState>.That.Matches(state =>
                        RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = state })
                        == RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = reference })),
                    A<Func<bool>>._, false))
                .MustHaveHappened();
        }

        [Test]
        public async Task NativeRecovery_RedrainsActionStartedDuringPrepareReceipt()
        {
            if (!RealTimePresentationRuntime.Enabled) return;
            var (host, client, hostNetwork, clientNetwork, queued, clientCombat) =
                CreatePair(nativeRecovery: true);
            var hostCombat = (ICombatInteractionService)typeof(MultiplayerActorBase)
                .GetProperty("CombatInteraction", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(host);
            var atomicChecks = 0;
            A.CallTo(() => hostCombat.HasActiveRealTimeAtomicActions())
                .ReturnsLazily(() => ++atomicChecks == 2);
            A.CallTo(() => NativeInteraction(host).SetPauseAsync(false))
                .Returns(Task.CompletedTask);
            A.CallTo(() => NativeInteraction(host).SetPauseAsync(true))
                .Returns(Task.CompletedTask);

            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);
            A.CallTo(() => clientCombat.GetRealTimeCombatStateAsync())
                .Returns(Task.FromResult(new NetworkCombatState
                    { Units = [new NetworkUnit { Id = "mismatch" }] }));
            await host.BeginRealTimeRepairAsync();
            await PumpRecovery(queued, hostNetwork, clientNetwork, host, client);

            AssertRecovered(host, client, clientCombat);
            Assert.That(atomicChecks, Is.GreaterThanOrEqualTo(3));
            A.CallTo(() => NativeInteraction(host).SetPauseAsync(false)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void NativeRecovery_ParticipantSelectionSkipsCorpsesAndBystanders()
        {
            var reference = new NetworkCombatState
            {
                Units =
                [
                    Unit("hero", inCombat: true, finallyDead: false),
                    Unit("dead-enemy", inCombat: true, finallyDead: true),
                    Unit("bystander", inCombat: false, finallyDead: false),
                    Unit("hero", inCombat: true, finallyDead: false)
                ]
            };

            Assert.That(MultiplayerClient.SelectLivingCombatParticipants(reference),
                Is.EqualTo(new[] { "hero" }));

            static NetworkUnit Unit(string id, bool inCombat, bool finallyDead) => new()
            {
                Id = id,
                Descriptor = new NetworkUnitDescriptor
                {
                    Vitals = new NetworkUnitVitals
                        { IsInCombat = inCombat, IsFinallyDead = finallyDead }
                }
            };
        }

        // Only Unity capture/load are synthetic. Real actors exchange archive bytes,
        // verify the reference and Commit before the repair Resume handshake.
        private static void ConfigureNativeRecoveryPair(MultiplayerHost host, MultiplayerClient client,
            INetworkHostConnection network, Queue<object> queued, ICombatInteractionService clientCombat)
        {
            foreach (var player in host.Game.Players)
                player.ContentState.RealTimeBuildFingerprint = new string('A', 64);
            client.Game.Players.Add(new NetworkPlayer(42));
            client.Game.Players[0].ContentState.RealTimeBuildFingerprint = new string('A', 64);
            A.CallTo(() => NativeInteraction(host).IsPaused).Returns(true);
            A.CallTo(() => NativeInteraction(client).IsPaused).Returns(true);
            A.CallTo(() => network.Send(42, A<object>._))
                .Invokes(call => { lock (queued) queued.Enqueue(call.Arguments[1]); });
            host.NativeCheckpointCapture = (session, checkpoint, authorized) =>
            {
                Assert.That(authorized(), Is.True);
                var source = new RealTimeMemorySaver();
                source.SaveJson("header", "{\"GameId\":\"integration-session\"}");
                source.SaveJson("player", "{\"synthetic\":true}");
                source.SaveJson("party", "{}");
                source.Seal();
                return Task.FromResult(source);
            };
            client.NativeCheckpointLoad = (source, session, checkpoint, authorized) =>
            {
                Assert.That(authorized(), Is.True);
                Assert.That(source.ReadJson("player"), Does.Contain("synthetic"));
                return Task.FromResult(new RealTimeNativeWorldLoadResult
                    { World = new object(), RepairObligation = new object() });
            };
            A.CallTo(() => clientCombat.TryRestoreNativeReferenceStateAsync(A<object>._, A<object>._,
                    A<NetworkCombatState>._, A<Func<bool>>._))
                .ReturnsLazily((object world, object obligation, NetworkCombatState state, Func<bool> guard) =>
                    Task.FromResult(guard() ? state : null));
            A.CallTo(() => clientCombat.TryCommitNativeReferenceStateAsync(A<object>._, A<object>._,
                    A<NetworkCombatState>._, A<Func<bool>>._))
                .ReturnsLazily((object world, object obligation, NetworkCombatState state, Func<bool> guard) =>
                    Task.FromResult(guard()));
            A.CallTo(() => clientCombat.EnsureUnitsInCombatAsync(A<List<string>>._))
                .Returns(new List<string>());
        }

        private static async Task PumpRecovery(Queue<object> queued, INetworkHostConnection hostNetwork,
            INetworkClientConnection clientNetwork, MultiplayerHost host, MultiplayerClient client)
        {
            for (var i = 0; i < 5000; i++)
            {
                Pump(queued, hostNetwork, clientNetwork);
                if (host.Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Stable
                    && client.Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Stable) return;
                if (host.Game.RealTimeCombat.RecoveryStatus == "stopped") return;
                await Task.Delay(1);
            }
            Assert.Fail("Recovery did not settle after the bounded transport pump.");
        }

        private static void AssertRecovered(MultiplayerHost host, MultiplayerClient client,
            ICombatInteractionService combat)
        {
            var expected = RealTimePresentationRuntime.Enabled ? CombatMode.RealTimeWithPause : CombatMode.TurnBased;
            Assert.That(host.Game.RealTimeCombat.Mode, Is.EqualTo(expected));
            Assert.That(client.Game.RealTimeCombat.Mode, Is.EqualTo(expected));
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(host.Game.RealTimeCombat.CanonicalStateHash, Is.EqualTo(client.Game.RealTimeCombat.CanonicalStateHash));
            if (RealTimePresentationRuntime.Enabled)
            {
                A.CallTo(() => combat.TryCommitNativeReferenceStateAsync(A<object>._, A<object>._,
                    A<NetworkCombatState>._, A<Func<bool>>._)).MustHaveHappenedOnceExactly();
                A.CallTo(() => combat.EnsureUnitsInCombatAsync(A<List<string>>._))
                    .MustHaveHappenedOnceExactly();
            }
        }
    }
}
