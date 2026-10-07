using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class RealTimeProtocolIntegrationTests
    {
        private static object SeedNativeVerificationHost(MultiplayerHost host, NotifyRealTimeNativeCheckpoint prepare,
            NotifyRealTimeNativeCheckpoint manifest, NetworkCombatState reference)
        {
            host.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            host.Game.RealTimeCombat.Phase = CombatSynchronizationPhase.Repairing;
            host.Game.RealTimeCombat.Epoch = prepare.Epoch;
            host.Game.RealTimeCombat.LastAuthoritativeSequence = prepare.LastIncludedSequence;
            A.CallTo(() => NativeInteraction(host).IsPaused).Returns(true);
            var type = typeof(MultiplayerHost).GetNestedType("NativeTransfer", BindingFlags.NonPublic);
            var owner = Activator.CreateInstance(type, nonPublic: true);
            void Set(string name, object value) => type.GetField(name).SetValue(owner, value);
            var bytes = RealTimeNativeReferenceCodec.Encode(new WOTRMultiplayer.Networking.Messages.Contracts.NetworkCombatState());
            Set("Session", host.Game); Set("Combat", host.Game.Combat); Set("State", host.Game.RealTimeCombat);
            Set("Phase", CombatSynchronizationPhase.Repairing); Set("BarrierOwned", (Func<bool>)(() => true));
            Set("Peer", 42L); Set("Prepare", prepare); Set("Manifest", manifest); Set("LoadRequested", true);
            Set("Reference", reference); Set("ReferenceBytes", bytes); Set("ReferenceHash", RealTimeNativeCheckpointLimits.Hash(bytes));
            Set("Deadline", DateTime.UtcNow.AddMinutes(2));
            foreach (var field in new[] { "Prepared", "Transferred", "Loaded" })
                ((TaskCompletionSource<bool>)type.GetField(field).GetValue(owner)).SetResult(true);
            typeof(MultiplayerHost).GetField("_nativeTransfer", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(host, owner);
            return owner;
        }

        [TestCase("success")]
        [TestCase("canonical-mismatch")]
        [TestCase("payload-mismatch")]
        [TestCase("restore-mismatch")]
        [TestCase("invalid-format")]
        public async Task NativeVerification_TwoActorsBindReferenceReceiptWithoutCommittingMechanics(string scenario)
        {
            var (host, client, hostNetwork, clientNetwork, queued, combat) = CreatePair();
            var prepare = NativePrepare(client);
            var reference = new NetworkCombatState();
            prepare.CanonicalStateHash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = reference });
            NativeHeader(clientNetwork, prepare);
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(queued, Is.Empty); return; }
            var (manifest, bytes) = NativeArchive(prepare); NativeHeader(clientNetwork, manifest);
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            foreach (var chunk in sender.GetDueChunks(DateTime.UtcNow))
                FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeNativeCheckpointChunk>(clientNetwork)(NetworkConstants.HostPlayerId, chunk);
            var world = new object(); var obligation = new object();
            RealTimeNativeGraphRepair.Add(world, obligation);
            var operation = new RealTimeNativeLoadOperation(() => true);
            await operation.RunAsync(() => Task.FromResult(new RealTimeNativeWorldLoadResult { World = world, RepairObligation = obligation }));
            typeof(MultiplayerClient).GetField("_nativeLoadOperation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, operation);
            A.CallTo(() => combat.TryRestoreNativeReferenceStateAsync(world, obligation, A<NetworkCombatState>._, A<Func<bool>>._))
                .Returns(Task.FromResult(scenario == "restore-mismatch" ? new NetworkCombatState
                { AreaEffects = new() { new() { Id = "unexpected-area" } } } : reference));
            SeedNativeVerificationHost(host, prepare, manifest, reference);
            queued.Clear();
            A.CallTo(() => hostNetwork.Send(42, A<object>._)).Invokes(call => queued.Enqueue(call.Arguments[1]));
            var verified = host.VerifyCurrentNativeReferenceAsync(manifest, () => true);
            var verify = (NotifyRealTimeNativeCheckpoint)queued.Dequeue();
            Assert.That(verify.Stage, Is.EqualTo((int)RealTimeNativeCheckpointStage.Verify));
            if (scenario == "invalid-format")
            {
                verify.ReferenceStateBytes[0] ^= 1;
                verify.ReferenceStateHash = RealTimeNativeCheckpointLimits.Hash(verify.ReferenceStateBytes);
            }
            NativeHeader(clientNetwork, ProtoBuf.Serializer.DeepClone(verify));
            var first = (ClientRealTimeNativeCheckpointAck)queued.Dequeue();
            var handler = FakeUtils.GetNetworkReceiverHandler<ClientRealTimeNativeCheckpointAck>(hostNetwork);
            if (scenario == "restore-mismatch" || scenario == "invalid-format")
            {
                Assert.That(first.Stage, Is.EqualTo((int)RealTimeNativeCheckpointAckStage.Failed));
                handler(42, first);
            }
            else
            {
                Assert.That(first.Stage, Is.EqualTo((int)RealTimeNativeCheckpointAckStage.ReferenceVerified));
                Assert.That(verified.IsCompleted, Is.False); // Drop the first reply.
                NativeHeader(clientNetwork, verify);
                var ack = (ClientRealTimeNativeCheckpointAck)queued.Dequeue();
                A.CallTo(() => combat.TryRestoreNativeReferenceStateAsync(world, obligation, A<NetworkCombatState>._, A<Func<bool>>._))
                    .MustHaveHappenedOnceExactly();
                handler(99, ack);
                Assert.That(verified.IsCompleted, Is.False);
                if (scenario == "canonical-mismatch") ack.CanonicalStateHash++;
                if (scenario == "payload-mismatch") ack.ReferenceStateHash = new string('B', 64);
                handler(42, ProtoBuf.Serializer.DeepClone(ack));
            }
            Assert.That(await verified, Is.EqualTo(scenario == "success"));
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(RealTimeNativeGraphRepair.Contains(world, obligation), Is.True);
            Assert.That(client.Game.RealTimeCombat.LastAuthoritativeSequence, Is.Zero);
            Assert.That(host.Game.RealTimeCombat.BlocksSaving && client.Game.RealTimeCombat.BlocksSaving, Is.True);
            A.CallTo(() => NativeInteraction(host).SetPauseAsync(false)).MustNotHaveHappened();
            A.CallTo(() => NativeInteraction(client).SetPauseAsync(false)).MustNotHaveHappened();
            host.Reset(); RealTimeNativeGraphRepair.Remove(world, obligation);
        }

        [TestCase(RealTimeRepairStage.Verify)]
        [TestCase(RealTimeRepairStage.Apply)]
        [TestCase(RealTimeRepairStage.Fallback)]
        [TestCase(RealTimeRepairStage.Resume)]
        public void NativeVerification_ClientIgnoresLegacyRepairForOwnedEpoch(RealTimeRepairStage stage)
        {
            var (_, client, _, network, queued, combat) = CreatePair();
            var prepare = NativePrepare(client); NativeHeader(network, prepare);
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(queued, Is.Empty); return; }
            queued.Clear();
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRepair>(network)(NetworkConstants.HostPlayerId, new NotifyRealTimeRepair
            {
                Epoch = prepare.Epoch, Stage = (int)stage, Mode = (int)CombatMode.RealTimeWithPause,
                CanonicalStateHash = prepare.CanonicalStateHash,
                State = new WOTRMultiplayer.Networking.Messages.Contracts.NetworkCombatState()
            });
            Assert.That(queued, Is.Empty);
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            A.CallTo(() => combat.GetRealTimeCombatStateAsync()).MustNotHaveHappened();
            A.CallTo(() => combat.TryApplyAndReadRealTimeCombatStateAsync(A<NetworkCombatState>._, A<Func<bool>>._)).MustNotHaveHappened();
            A.CallTo(() => NativeInteraction(client).SetPauseAsync(false)).MustNotHaveHappened();
        }

        [Test]
        public void NativeVerification_HostIgnoresLegacyAckWhileNativeBarrierOwned()
        {
            var (host, client, hostNetwork, _, queued, _) = CreatePair();
            var prepare = NativePrepare(client);
            if (!RealTimePresentationRuntime.Enabled) return;
            var (manifest, _) = NativeArchive(prepare);
            SeedNativeVerificationHost(host, prepare, manifest, new NetworkCombatState());
            typeof(MultiplayerHost).GetField("_realTimeRepairStage", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(host, (RealTimeRepairStage?)RealTimeRepairStage.Apply);
            host.Game.RealTimeCombat.RequiredPlayers = new() { host.Game.LocalPlayerId, 42 };
            host.Game.RealTimeCombat.ReadyPlayers = new() { host.Game.LocalPlayerId };
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeRepairAck>(hostNetwork)(42, new ClientRealTimeRepairAck
            { Epoch = prepare.Epoch, Stage = (int)RealTimeRepairStage.Apply, PlayerId = 42, Applied = true });
            Assert.That(host.Game.RealTimeCombat.ReadyPlayers.Contains(42), Is.False);
            Assert.That(queued, Is.Empty);
            host.Reset();
        }
    }
}
