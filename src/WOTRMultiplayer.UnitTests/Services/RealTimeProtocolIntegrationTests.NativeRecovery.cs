using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Abstractions;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class RealTimeProtocolIntegrationTests
    {
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
                A.CallTo(() => combat.TryCommitNativeReferenceStateAsync(A<object>._, A<object>._,
                    A<NetworkCombatState>._, A<Func<bool>>._)).MustHaveHappenedOnceExactly();
        }
    }
}
