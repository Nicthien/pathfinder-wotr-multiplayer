using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Abstractions;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class RealTimeProtocolIntegrationTests
    {
        private static (Type Type, object Transfer) BindNativeAbortHost(MultiplayerHost host, INetworkHostConnection network,
            Queue<object> queued, NotifyRealTimeNativeCheckpoint prepare,
            NotifyRealTimeNativeCheckpoint manifest = null)
        {
            A.CallTo(() => network.Send(42, A<object>._)).Invokes(call => queued.Enqueue(call.Arguments[1]));
            host.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            host.Game.RealTimeCombat.Phase = CombatSynchronizationPhase.Repairing;
            host.Game.RealTimeCombat.Epoch = prepare.Epoch;
            host.Game.RealTimeCombat.LastAuthoritativeSequence = prepare.LastIncludedSequence;
            var type = typeof(MultiplayerHost).GetNestedType("NativeTransfer", BindingFlags.NonPublic);
            var transfer = Activator.CreateInstance(type, nonPublic: true);
            void Set(string name, object value) => type.GetField(name).SetValue(transfer, value);
            Set("Session", host.Game); Set("Combat", host.Game.Combat); Set("State", host.Game.RealTimeCombat);
            Set("Phase", CombatSynchronizationPhase.Repairing); Set("BarrierOwned", (Func<bool>)(() => true));
            Set("Peer", 42L); Set("Prepare", prepare); Set("Manifest", manifest); Set("Deadline", DateTime.UtcNow.AddMinutes(1));
            typeof(MultiplayerHost).GetField("_nativeTransfer", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(host, transfer);
            return (type, transfer);
        }

        [TestCase("lost-prepare")][TestCase("prepare")][TestCase("manifest")][TestCase("bytes")]
        public async Task NativeAbort_TwoActorsConfirmRevocationWithoutLoadingOrResuming(string stage)
        {
            var (host, client, hostNetwork, network, queued, combat) = CreatePair();
            var prepare = NativePrepare(client);
            if (!RealTimePresentationRuntime.Enabled) return;
            var (manifest, bytes) = NativeArchive(prepare);
            if (stage != "lost-prepare") NativeHeader(network, prepare);
            if (stage == "manifest" || stage == "bytes") NativeHeader(network, manifest);
            if (stage == "bytes")
            {
                using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
                foreach (var chunk in sender.GetDueChunks(DateTime.UtcNow))
                    FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeNativeCheckpointChunk>(network)(NetworkConstants.HostPlayerId, chunk);
            }
            BindNativeAbortHost(host, hostNetwork, queued, prepare, stage == "prepare" ? null : manifest);
            queued.Clear();
            var aborted = host.AbortCurrentNativeCheckpointAsync();
            Assert.That(aborted.IsCompleted, Is.False);
            var request = (NotifyRealTimeNativeCheckpoint)queued.Dequeue();
            NativeHeader(network, request);
            var ack = (ClientRealTimeNativeCheckpointAck)queued.Dequeue();
            Assert.That(ack.Stage, Is.EqualTo((int)RealTimeNativeCheckpointAckStage.Aborted));
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeNativeCheckpointAck>(hostNetwork)(42, ack);
            Assert.That(await aborted, Is.True);
            Assert.That(await host.AbortCurrentNativeCheckpointAsync(), Is.True);
            NativeHeader(network, request); // Lost receipt: return the same outcome, no load.
            Assert.That(((ClientRealTimeNativeCheckpointAck)queued.Dequeue()).Stage, Is.EqualTo((int)RealTimeNativeCheckpointAckStage.Aborted));
            Assert.Throws<InvalidOperationException>(() => client.CloneTransferredNativeCheckpoint(manifest));
            Assert.That(host.Game.RealTimeCombat.BlocksSaving && client.Game.RealTimeCombat.BlocksSaving, Is.True);
            Assert.That(host.Game.RealTimeCombat.LastAuthoritativeSequence, Is.EqualTo(prepare.LastIncludedSequence));
            Assert.That(client.Game.RealTimeCombat.LastAuthoritativeSequence, Is.Zero);
            A.CallTo(() => combat.TryApplyAndReadRealTimeCombatStateAsync(A<NetworkCombatState>._, A<Func<bool>>._)).MustNotHaveHappened();
            A.CallTo(() => NativeInteraction(client).SetPauseAsync(false)).MustNotHaveHappened();
            A.CallTo(() => NativeInteraction(host).SetPauseAsync(false)).MustNotHaveHappened();
            host.Reset(); client.Reset();
        }

        [Test]
        public async Task NativeAbort_PendingLoadMustSettleBeforeClientAcknowledges()
        {
            var (_, client, _, network, queued, _) = CreatePair();
            var prepare = NativePrepare(client); NativeHeader(network, prepare);
            if (!RealTimePresentationRuntime.Enabled) return;
            var pending = new TaskCompletionSource<RealTimeNativeWorldLoadResult>();
            var operation = new RealTimeNativeLoadOperation(() => true);
            var task = operation.RunAsync(() => pending.Task);
            typeof(MultiplayerClient).GetField("_nativeLoadOperation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, operation);
            queued.Clear();
            var abort = RealTimeNativeCheckpointLimits.Copy(prepare); abort.Stage = (int)RealTimeNativeCheckpointStage.Abort;
            NativeHeader(network, abort); NativeHeader(network, abort);
            Assert.That(queued, Is.Empty);
            pending.SetException(new InvalidOperationException("synthetic-load-failure"));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
            client.TickRealTimePresentation();
            Assert.That(((ClientRealTimeNativeCheckpointAck)queued.Dequeue()).Stage, Is.EqualTo((int)RealTimeNativeCheckpointAckStage.Aborted));
            client.TickRealTimePresentation(); Assert.That(queued, Is.Empty);
            Assert.That(client.Game.RealTimeCombat.BlocksSaving, Is.True);
            client.Reset(); await Task.CompletedTask;
        }

        [Test]
        public void NativeAbort_PendingReferenceMutationCannotBeAcknowledged()
        {
            var (_, client, _, network, queued, _) = CreatePair();
            var prepare = NativePrepare(client); NativeHeader(network, prepare);
            if (!RealTimePresentationRuntime.Enabled) return;
            var owner = typeof(MultiplayerClient).GetField("_nativeReferenceRestorationOwner", BindingFlags.Instance | BindingFlags.NonPublic);
            owner.SetValue(client, new object()); queued.Clear();
            var abort = RealTimeNativeCheckpointLimits.Copy(prepare); abort.Stage = (int)RealTimeNativeCheckpointStage.Abort;
            NativeHeader(network, abort); Assert.That(queued, Is.Empty);
            owner.SetValue(client, null); client.TickRealTimePresentation();
            Assert.That(((ClientRealTimeNativeCheckpointAck)queued.Dequeue()).Stage, Is.EqualTo((int)RealTimeNativeCheckpointAckStage.Aborted));
            client.Reset();
        }

        [Test]
        public void NativeAbort_LostPrepareStillWaitsForTheMainThreadPauseReceipt()
        {
            var actions = new Queue<Action>();
            var (_, client, _, network, queued, _) = CreatePair(mainThreadQueue: actions);
            var prepare = NativePrepare(client);
            if (!RealTimePresentationRuntime.Enabled) return;
            var abort = RealTimeNativeCheckpointLimits.Copy(prepare); abort.Stage = (int)RealTimeNativeCheckpointStage.Abort;
            NativeHeader(network, abort); Assert.That(queued, Is.Empty);
            actions.Dequeue()(); Assert.That(queued, Is.Empty);
            Assert.That(actions.Count, Is.EqualTo(1));
            actions.Dequeue()();
            Assert.That(((ClientRealTimeNativeCheckpointAck)queued.Dequeue()).Stage, Is.EqualTo((int)RealTimeNativeCheckpointAckStage.Aborted));
            Assert.That(client.Game.RealTimeCombat.BlocksSaving, Is.True);
            client.Reset();
        }

        [TestCase("session")][TestCase("save")][TestCase("epoch")][TestCase("build")][TestCase("checkpoint")]
        [TestCase("peer")][TestCase("unpaused")]
        public void NativeAbort_ClientCannotAcknowledgeAnotherIdentityOrAnUnconfirmedPause(string fault)
        {
            var (_, client, _, network, queued, _) = CreatePair();
            var prepare = NativePrepare(client); NativeHeader(network, prepare);
            if (!RealTimePresentationRuntime.Enabled) return;
            queued.Clear();
            var abort = RealTimeNativeCheckpointLimits.Copy(prepare); abort.Stage = (int)RealTimeNativeCheckpointStage.Abort;
            switch (fault)
            {
                case "session": abort.SessionId += "old"; break;
                case "save": abort.LoadedSaveSeed++; break;
                case "epoch": abort.Epoch++; break;
                case "build": abort.BuildFingerprint = new string('B', 64); break;
                case "checkpoint": abort.CheckpointId = new string('2', 32); break;
                case "unpaused": A.CallTo(() => NativeInteraction(client).IsPaused).Returns(false); break;
            }
            NativeHeader(network, abort, fault == "peer" ? 99 : NetworkConstants.HostPlayerId);
            client.TickRealTimePresentation(); Assert.That(queued, Is.Empty);
            Assert.That(client.Game.RealTimeCombat.BlocksSaving, Is.True);
            client.Reset();
        }

        [TestCase("pending")][TestCase("acknowledged")][TestCase("expired")]
        public void NativeAbort_LegacyRepairCannotReleaseTheRetainedNativeBarrier(string outcome)
        {
            var (host, client, hostNetwork, _, queued, _) = CreatePair();
            var prepare = NativePrepare(client);
            if (!RealTimePresentationRuntime.Enabled) return;
            var (type, transfer) = BindNativeAbortHost(host, hostNetwork, queued, prepare);
            var task = host.AbortCurrentNativeCheckpointAsync(); queued.Clear();
            if (outcome == "expired") type.GetField("AbortExpired").SetValue(transfer, true);
            if (outcome == "acknowledged")
                FakeUtils.GetNetworkReceiverHandler<ClientRealTimeNativeCheckpointAck>(hostNetwork)(42, new ClientRealTimeNativeCheckpointAck
                {
                    SessionId = prepare.SessionId, LoadedSaveSeed = prepare.LoadedSaveSeed, Epoch = prepare.Epoch,
                    CheckpointId = prepare.CheckpointId, Stage = (int)RealTimeNativeCheckpointAckStage.Aborted
                });
            host.Game.RealTimeCombat.RequiredPlayers.Add(42);
            typeof(MultiplayerHost).GetField("_realTimeRepairStage", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(host, (RealTimeRepairStage?)RealTimeRepairStage.Apply);
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeRepairAck>(hostNetwork)(42, new ClientRealTimeRepairAck
            { Epoch = prepare.Epoch, Stage = (int)RealTimeRepairStage.Apply, PlayerId = 42, Applied = true });
            Assert.That(host.Game.RealTimeCombat.ReadyPlayers, Is.Empty);
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(queued, Is.Empty);
            host.Reset(); client.Reset();
        }

        [TestCase("peer")][TestCase("save")][TestCase("epoch")][TestCase("checkpoint")][TestCase("number")]
        public void NativeAbort_HostCannotAcceptForeignOrMalformedReceipt(string fault)
        {
            var (host, client, hostNetwork, _, queued, _) = CreatePair();
            var prepare = NativePrepare(client);
            if (!RealTimePresentationRuntime.Enabled) return;
            BindNativeAbortHost(host, hostNetwork, queued, prepare);
            var task = host.AbortCurrentNativeCheckpointAsync();
            var request = (NotifyRealTimeNativeCheckpoint)queued.Dequeue();
            var ack = new ClientRealTimeNativeCheckpointAck
            {
                SessionId = request.SessionId, LoadedSaveSeed = request.LoadedSaveSeed, Epoch = request.Epoch,
                CheckpointId = request.CheckpointId, ArchiveHash = request.ArchiveHash, Stage = (int)RealTimeNativeCheckpointAckStage.Aborted
            };
            switch (fault)
            {
                case "save": ack.LoadedSaveSeed++; break;
                case "epoch": ack.Epoch++; break;
                case "checkpoint": ack.CheckpointId = new string('2', 32); break;
                case "number": ack.Number = 1; break;
            }
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeNativeCheckpointAck>(hostNetwork)(fault == "peer" ? 99 : 42, ack);
            Assert.That(task.IsCompleted, Is.False);
            Assert.That(host.Game.RealTimeCombat.BlocksSaving, Is.True);
            host.Reset(); client.Reset();
        }

        [Test]
        public async Task NativeAbort_ExpiredWaitRemainsAFailureAndKeepsItsBarrierTombstone()
        {
            var (host, client, hostNetwork, _, queued, _) = CreatePair();
            var prepare = NativePrepare(client);
            if (!RealTimePresentationRuntime.Enabled) return;
            var (type, transfer) = BindNativeAbortHost(host, hostNetwork, queued, prepare);
            var task = host.AbortCurrentNativeCheckpointAsync(); queued.Clear();
            type.GetField("Abort").SetValue(transfer, new RealTimeNativeCheckpointAbort(42, prepare,
                DateTime.UtcNow - RealTimeNativeCheckpointAbort.MaximumWait - TimeSpan.FromSeconds(1)));
            typeof(MultiplayerHost).GetMethod("TickNativeCheckpointTransfer", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(host, null);
            Assert.That(await task, Is.False);
            Assert.That(type.GetField("AbortExpired").GetValue(transfer), Is.True);
            Assert.That(typeof(MultiplayerHost).GetField("_nativeTransfer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(host), Is.SameAs(transfer));
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeNativeCheckpointAck>(hostNetwork)(42, new ClientRealTimeNativeCheckpointAck
            {
                SessionId = prepare.SessionId, LoadedSaveSeed = prepare.LoadedSaveSeed, Epoch = prepare.Epoch,
                CheckpointId = prepare.CheckpointId, Stage = (int)RealTimeNativeCheckpointAckStage.Aborted
            });
            Assert.That(await host.AbortCurrentNativeCheckpointAsync(), Is.False);
            Assert.That(host.Game.RealTimeCombat.BlocksSaving, Is.True);
            host.Reset(); client.Reset();
        }
    }
}
