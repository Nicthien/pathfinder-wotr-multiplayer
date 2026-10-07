using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Entities;
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
        private static IGameInteractionService NativeInteraction(MultiplayerActorBase client) =>
            (IGameInteractionService)typeof(MultiplayerActorBase).GetProperty("GameInteraction", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);

        private static NotifyRealTimeNativeCheckpoint NativePrepare(MultiplayerClient client)
        {
            client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            client.Game.RealTimeCombat.Phase = CombatSynchronizationPhase.Repairing;
            client.Game.RealTimeCombat.Epoch = 3;
            client.Game.Players.Add(new NetworkPlayer(client.Game.LocalPlayerId));
            client.Game.Players.Last().ContentState.RealTimeBuildFingerprint = new string('A', 64);
            A.CallTo(() => NativeInteraction(client).IsPaused).Returns(true);
            return new NotifyRealTimeNativeCheckpoint
            {
                SessionId = client.Game.Id, LoadedSaveSeed = client.Game.LoadedSaveSeed, Epoch = 3,
                CheckpointId = "11111111111111111111111111111111", BuildFingerprint = new string('A', 64),
                LastIncludedSequence = 7, CanonicalStateHash = 123, Stage = (int)RealTimeNativeCheckpointStage.Prepare
            };
        }

        private static (NotifyRealTimeNativeCheckpoint Manifest, byte[] Archive) NativeArchive(NotifyRealTimeNativeCheckpoint prepare)
        {
            using var source = new RealTimeMemorySaver();
            source.SaveJson("header", "{\"GameId\":\"integration-session\"}");
            source.SaveJson("player", "{\"synthetic\":\"" + new string('x', 70000) + "\"}");
            source.SaveJson("party", "{}"); source.Seal();
            var bytes = RealTimeNativeCheckpointCodec.Encode(source);
            var manifest = RealTimeNativeCheckpointLimits.Copy(prepare);
            manifest.Stage = (int)RealTimeNativeCheckpointStage.Manifest;
            manifest.ArchiveHash = RealTimeNativeCheckpointLimits.Hash(bytes); manifest.ArchiveBytes = bytes.Length;
            manifest.ChunkBytes = RealTimeNativeCheckpointLimits.ChunkBytes;
            manifest.ChunkCount = (bytes.Length + manifest.ChunkBytes - 1) / manifest.ChunkBytes;
            Assert.That(RealTimeNativeCheckpointCodec.MaximumArchiveBytes, Is.EqualTo(RealTimeNativeCheckpointLimits.MaximumArchiveBytes));
            return (manifest, bytes);
        }

        private static void NativeHeader(INetworkClientConnection network, NotifyRealTimeNativeCheckpoint header, long peer = NetworkConstants.HostPlayerId)
            => FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeNativeCheckpoint>(network)(peer, header);

        [Test]
        public void NativeLoad_RequestBeforeVerifiedBytesCannotScheduleWorldReplacement()
        {
            var (_, client, _, network, queued, _) = CreatePair();
            var prepare = NativePrepare(client); NativeHeader(network, prepare);
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(queued, Is.Empty); return; }
            var (manifest, _) = NativeArchive(prepare); NativeHeader(network, manifest);
            queued.Clear();
            var load = RealTimeNativeCheckpointLimits.Copy(manifest); load.Stage = (int)RealTimeNativeCheckpointStage.Load;
            NativeHeader(network, load);
            Assert.That(queued, Is.Empty);
            Assert.That(typeof(MultiplayerClient).GetField("_nativeLoadOperation", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client), Is.Null);
            Assert.That(RealTimeNativeLoadLease.IsActive, Is.False);
        }

        [Test]
        public async System.Threading.Tasks.Task NativeLoad_RepeatedRequestReturnsExistingReceiptWithoutLoadingGame()
        {
            var (_, client, _, network, queued, _) = CreatePair();
            var prepare = NativePrepare(client); NativeHeader(network, prepare);
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(queued, Is.Empty); return; }
            var (manifest, bytes) = NativeArchive(prepare); NativeHeader(network, manifest);
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            foreach (var chunk in sender.GetDueChunks(DateTime.UtcNow))
                FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeNativeCheckpointChunk>(network)(NetworkConstants.HostPlayerId, chunk);
            // Seed only a synthetic successful operation. Never invoke the native loader.
            var operation = new RealTimeNativeLoadOperation(() => true);
            await operation.RunAsync(() => System.Threading.Tasks.Task.FromResult(new RealTimeNativeWorldLoadResult
            { World = new object(), RepairObligation = new object() }));
            typeof(MultiplayerClient).GetField("_nativeLoadOperation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, operation);
            queued.Clear();
            var load = RealTimeNativeCheckpointLimits.Copy(manifest); load.Stage = (int)RealTimeNativeCheckpointStage.Load;
            NativeHeader(network, load); NativeHeader(network, load);
            Assert.That(queued.OfType<ClientRealTimeNativeCheckpointAck>().Count(), Is.EqualTo(2));
            Assert.That(queued.OfType<ClientRealTimeNativeCheckpointAck>().All(ack => ack.Stage == (int)RealTimeNativeCheckpointAckStage.Loaded), Is.True);
            Assert.That(RealTimeNativeLoadLease.IsActive, Is.False);
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(client.Game.RealTimeCombat.BlocksSaving, Is.True);
            A.CallTo(() => NativeInteraction(client).SetPauseAsync(false)).MustNotHaveHappened();
        }

        [Test]
        public void NativeTransfer_ClientEndpointAssemblesSourceButNeverAppliesMechanicsOrResumes()
        {
            var (_, client, _, network, queued, combat) = CreatePair();
            var prepare = NativePrepare(client);
            NativeHeader(network, prepare);
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(queued, Is.Empty); return; }
            Assert.That(((ClientRealTimeNativeCheckpointAck)queued.Dequeue()).Stage, Is.EqualTo((int)RealTimeNativeCheckpointAckStage.Prepared));
            var (manifest, bytes) = NativeArchive(prepare);
            NativeHeader(network, manifest);
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            var chunkHandler = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeNativeCheckpointChunk>(network);
            foreach (var chunk in sender.GetDueChunks(DateTime.UtcNow).AsEnumerable().Reverse()) chunkHandler(NetworkConstants.HostPlayerId, chunk);
            using var source = client.CloneTransferredNativeCheckpoint(manifest);
            Assert.That(source.ReadJson("player"), Does.Contain("synthetic"));
            Assert.That(queued.OfType<ClientRealTimeNativeCheckpointAck>().Last().Stage, Is.EqualTo((int)RealTimeNativeCheckpointAckStage.Received));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(client.Game.RealTimeCombat.BlocksSaving, Is.True);
            Assert.That(client.Game.RealTimeCombat.LastAuthoritativeSequence, Is.Zero);
            A.CallTo(() => combat.TryApplyAndReadRealTimeCombatStateAsync(A<NetworkCombatState>._, A<Func<bool>>._)).MustNotHaveHappened();
            A.CallTo(() => NativeInteraction(client).SetPauseAsync(false)).MustNotHaveHappened();
            // A lost final receipt is repaired by the repeated manifest, without
            // rebuilding the source or calling gameplay callbacks a second time.
            queued.Clear(); NativeHeader(network, manifest);
            Assert.That(((ClientRealTimeNativeCheckpointAck)queued.Dequeue()).Stage, Is.EqualTo((int)RealTimeNativeCheckpointAckStage.Received));
        }

        [TestCase("session")]
        [TestCase("save")]
        [TestCase("epoch")]
        [TestCase("build")]
        [TestCase("foreign-peer")]
        public void NativeTransfer_ClientRejectsOldOrForeignPrepareBeforeAcknowledgement(string mutation)
        {
            var (_, client, _, network, queued, _) = CreatePair();
            var prepare = NativePrepare(client);
            switch (mutation)
            {
                case "session": prepare.SessionId += "old"; break;
                case "save": prepare.LoadedSaveSeed++; break;
                case "epoch": prepare.Epoch--; break;
                case "build": prepare.BuildFingerprint = new string('B', 64); break;
            }
            NativeHeader(network, prepare, mutation == "foreign-peer" ? 99 : NetworkConstants.HostPlayerId);
            Assert.That(queued, Is.Empty);
            Assert.Throws<InvalidOperationException>(() => client.CloneTransferredNativeCheckpoint(prepare));
        }

        [Test]
        public void NativeTransfer_CompetingCheckpointCannotReplacePreparedIdentityInSameEpoch()
        {
            var (_, client, _, network, queued, _) = CreatePair();
            var prepare = NativePrepare(client); NativeHeader(network, prepare);
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(queued, Is.Empty); return; }
            queued.Clear(); var competing = RealTimeNativeCheckpointLimits.Copy(prepare); competing.CheckpointId = new string('2', 32);
            NativeHeader(network, competing);
            Assert.That(queued, Is.Empty);
            var (manifest, bytes) = NativeArchive(prepare); NativeHeader(network, manifest);
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            foreach (var chunk in sender.GetDueChunks(DateTime.UtcNow))
                FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeNativeCheckpointChunk>(network)(NetworkConstants.HostPlayerId, chunk);
            using var source = client.CloneTransferredNativeCheckpoint(manifest);
            Assert.That(source.IsSealed, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeTransfer_AbortOrNewLoadedSaveRevokesCompletedSource(bool newLoad)
        {
            var (_, client, _, network, queued, _) = CreatePair();
            var prepare = NativePrepare(client); NativeHeader(network, prepare);
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(queued, Is.Empty); return; }
            var (manifest, bytes) = NativeArchive(prepare); NativeHeader(network, manifest);
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            foreach (var chunk in sender.GetDueChunks(DateTime.UtcNow))
                FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeNativeCheckpointChunk>(network)(NetworkConstants.HostPlayerId, chunk);
            using (var source = client.CloneTransferredNativeCheckpoint(manifest)) Assert.That(source.IsSealed, Is.True);
            if (newLoad) { client.Game.LoadedSaveSeed++; client.TickRealTimePresentation(); }
            else { var abort = RealTimeNativeCheckpointLimits.Copy(manifest); abort.Stage = (int)RealTimeNativeCheckpointStage.Abort; NativeHeader(network, abort); }
            Assert.Throws<InvalidOperationException>(() => client.CloneTransferredNativeCheckpoint(manifest));
            Assert.That(client.Game.RealTimeCombat.BlocksSaving, Is.True);
        }

        [Test]
        public void NativeTransfer_PreparedAckWaitsForMainThreadPauseCompletion()
        {
            var actions = new Queue<Action>();
            var (_, client, _, network, queued, _) = CreatePair(mainThreadQueue: actions);
            var prepare = NativePrepare(client); NativeHeader(network, prepare);
            Assert.That(queued, Is.Empty);
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(actions, Is.Empty); return; }
            actions.Dequeue()();
            Assert.That(queued, Is.Empty);
            Assert.That(actions.Count, Is.EqualTo(1));
            actions.Dequeue()();
            Assert.That(((ClientRealTimeNativeCheckpointAck)queued.Dequeue()).Stage, Is.EqualTo((int)RealTimeNativeCheckpointAckStage.Prepared));
        }

        [Test]
        public void NativeTransfer_UnconfirmedPauseCannotAcknowledgePreparedOrReceiveSource()
        {
            var (_, client, _, network, queued, _) = CreatePair();
            var prepare = NativePrepare(client);
            A.CallTo(() => NativeInteraction(client).IsPaused).Returns(false);
            NativeHeader(network, prepare);
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(queued, Is.Empty); return; }
            Assert.That(((ClientRealTimeNativeCheckpointAck)queued.Dequeue()).Stage, Is.EqualTo((int)RealTimeNativeCheckpointAckStage.Failed));
            Assert.Throws<InvalidOperationException>(() => client.CloneTransferredNativeCheckpoint(prepare));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeTransfer_HostReceiptIsPeerBoundAndDoesNotCommitMechanics(bool reset)
        {
            var (host, client, hostNetwork, _, queued, _) = CreatePair();
            var prepare = NativePrepare(client);
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(queued, Is.Empty); return; }
            host.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            host.Game.RealTimeCombat.Phase = CombatSynchronizationPhase.Repairing;
            host.Game.RealTimeCombat.Epoch = prepare.Epoch;
            host.Game.RealTimeCombat.LastAuthoritativeSequence = prepare.LastIncludedSequence;
            var (manifest, bytes) = NativeArchive(prepare);
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            sender.GetDueChunks(DateTime.UtcNow); // synthetic source; no native host capture
            var type = typeof(MultiplayerHost).GetNestedType("NativeTransfer", BindingFlags.NonPublic);
            var transfer = Activator.CreateInstance(type, nonPublic: true);
            void Set(string name, object value) => type.GetField(name).SetValue(transfer, value);
            Set("Session", host.Game); Set("Combat", host.Game.Combat); Set("State", host.Game.RealTimeCombat);
            Set("Phase", CombatSynchronizationPhase.Repairing); Set("BarrierOwned", (Func<bool>)(() => true));
            Set("Peer", 42L); Set("Prepare", prepare); Set("Manifest", manifest); Set("Sender", sender);
            Set("Deadline", DateTime.UtcNow.AddSeconds(60));
            typeof(MultiplayerHost).GetField("_nativeTransfer", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(host, transfer);
            var completion = (System.Threading.Tasks.TaskCompletionSource<bool>)type.GetField("Transferred").GetValue(transfer);
            var ack = new ClientRealTimeNativeCheckpointAck
            {
                SessionId = manifest.SessionId, LoadedSaveSeed = manifest.LoadedSaveSeed, Epoch = manifest.Epoch,
                CheckpointId = manifest.CheckpointId, ArchiveHash = manifest.ArchiveHash,
                Stage = (int)RealTimeNativeCheckpointAckStage.Received
            };
            var handler = FakeUtils.GetNetworkReceiverHandler<ClientRealTimeNativeCheckpointAck>(hostNetwork);
            handler(99, ack);
            Assert.That(completion.Task.IsCompleted, Is.False);
            if (reset)
            {
                host.Reset();
                Assert.That(completion.Task.Result, Is.False);
                Assert.Throws<ObjectDisposedException>(() => sender.GetDueChunks(DateTime.UtcNow));
                handler(42, ack); // late receipt cannot resurrect an old transfer
            }
            else
            {
                handler(42, ack);
                Assert.That(completion.Task.Result, Is.True);
                Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
                Assert.That(host.Game.RealTimeCombat.BlocksSaving, Is.True);
                A.CallTo(() => NativeInteraction(host).SetPauseAsync(false)).MustNotHaveHappened();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeLoad_RevokedBarrierOrOldManifestCannotReachNativeWorld(bool oldManifest)
        {
            var (_, client, _, network, queued, _) = CreatePair();
            var prepare = NativePrepare(client); NativeHeader(network, prepare);
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(queued, Is.Empty); return; }
            var (manifest, bytes) = NativeArchive(prepare); NativeHeader(network, manifest);
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            foreach (var chunk in sender.GetDueChunks(DateTime.UtcNow))
                FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeNativeCheckpointChunk>(network)(NetworkConstants.HostPlayerId, chunk);
            var requested = RealTimeNativeCheckpointLimits.Copy(manifest);
            if (oldManifest) requested.Epoch--;
            var failure = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await client.LoadTransferredNativeCheckpointAsync(requested, () => false));
            Assert.That(failure.Message, Is.EqualTo(oldManifest
                ? "native-transfer-source-not-ready-for-protected-load" : "native-load-client-barrier-not-authorized"));
            Assert.That(RealTimeNativeLoadLease.IsActive, Is.False);
            using var intact = client.CloneTransferredNativeCheckpoint(manifest);
            Assert.That(intact.IsSealed, Is.True);
            Assert.That(client.Game.RealTimeCombat.BlocksSaving, Is.True);
        }

        [TestCase("valid")]
        [TestCase("not-requested")]
        [TestCase("not-transferred")]
        [TestCase("foreign-peer")]
        [TestCase("old-save")]
        [TestCase("chunk-number")]
        public void NativeLoad_HostOnlyAcceptsRequestedLoadedReceiptAndNeverCommits(string mutation)
        {
            var (host, client, hostNetwork, _, queued, _) = CreatePair();
            var prepare = NativePrepare(client);
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(queued, Is.Empty); return; }
            host.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            host.Game.RealTimeCombat.Phase = CombatSynchronizationPhase.Repairing;
            host.Game.RealTimeCombat.Epoch = prepare.Epoch;
            host.Game.RealTimeCombat.LastAuthoritativeSequence = prepare.LastIncludedSequence;
            var (manifest, _) = NativeArchive(prepare);
            var type = typeof(MultiplayerHost).GetNestedType("NativeTransfer", BindingFlags.NonPublic);
            var transfer = Activator.CreateInstance(type, nonPublic: true);
            void Set(string name, object value) => type.GetField(name).SetValue(transfer, value);
            Set("Session", host.Game); Set("Combat", host.Game.Combat); Set("State", host.Game.RealTimeCombat);
            Set("Phase", CombatSynchronizationPhase.Repairing); Set("BarrierOwned", (Func<bool>)(() => true));
            Set("Peer", 42L); Set("Prepare", prepare); Set("Manifest", manifest);
            Set("LoadRequested", mutation != "not-requested"); Set("Deadline", DateTime.UtcNow.AddMinutes(2));
            typeof(MultiplayerHost).GetField("_nativeTransfer", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(host, transfer);
            var transferred = (System.Threading.Tasks.TaskCompletionSource<bool>)type.GetField("Transferred").GetValue(transfer);
            if (mutation != "not-transferred") transferred.SetResult(true);
            var loaded = (System.Threading.Tasks.TaskCompletionSource<bool>)type.GetField("Loaded").GetValue(transfer);
            var ack = new ClientRealTimeNativeCheckpointAck
            {
                SessionId = manifest.SessionId, LoadedSaveSeed = manifest.LoadedSaveSeed + (mutation == "old-save" ? 1 : 0),
                Epoch = manifest.Epoch, CheckpointId = manifest.CheckpointId, ArchiveHash = manifest.ArchiveHash,
                Number = mutation == "chunk-number" ? 1 : 0, Stage = (int)RealTimeNativeCheckpointAckStage.Loaded
            };
            var handler = FakeUtils.GetNetworkReceiverHandler<ClientRealTimeNativeCheckpointAck>(hostNetwork);
            handler(mutation == "foreign-peer" ? 99 : 42, ack);
            Assert.That(loaded.Task.IsCompleted, Is.EqualTo(mutation == "valid"));
            if (mutation == "valid")
            {
                var deadline = type.GetField("Deadline").GetValue(transfer);
                handler(42, ack);
                Assert.That(type.GetField("Deadline").GetValue(transfer), Is.EqualTo(deadline));
                Assert.That(loaded.Task.Result, Is.True);
            }
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(host.Game.RealTimeCombat.BlocksSaving, Is.True);
            A.CallTo(() => NativeInteraction(host).SetPauseAsync(false)).MustNotHaveHappened();
            host.Reset();
        }

        [TestCase("success")]
        [TestCase("wrong-reference")]
        [TestCase("unloaded")]
        [TestCase("revoked")]
        [TestCase("mismatch")]
        [TestCase("revoked-during-restore")]
        public async System.Threading.Tasks.Task NativeReference_RestoresOnlyLoadedManifestAndNeverLiftsGraphObligation(string scenario)
        {
            var (_, client, _, network, queued, combat) = CreatePair();
            var prepare = NativePrepare(client);
            var reference = new NetworkCombatState();
            prepare.CanonicalStateHash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = reference });
            NativeHeader(network, prepare);
            if (!RealTimePresentationRuntime.Enabled) { Assert.That(queued, Is.Empty); return; }
            var (manifest, bytes) = NativeArchive(prepare); NativeHeader(network, manifest);
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            foreach (var chunk in sender.GetDueChunks(DateTime.UtcNow))
                FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeNativeCheckpointChunk>(network)(NetworkConstants.HostPlayerId, chunk);
            var world = new object(); var obligation = new object();
            RealTimeNativeGraphRepair.Add(world, obligation);
            var operation = new RealTimeNativeLoadOperation(() => true);
            if (scenario != "unloaded")
                await operation.RunAsync(() => System.Threading.Tasks.Task.FromResult(new RealTimeNativeWorldLoadResult
                { World = world, RepairObligation = obligation }));
            typeof(MultiplayerClient).GetField("_nativeLoadOperation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, operation);
            var authorized = scenario != "revoked";
            var actual = scenario == "mismatch" ? new NetworkCombatState
                { AreaEffects = new List<WOTRMultiplayer.Entities.AreaEffects.NetworkAreaEffect> { new() { Id = "unexpected" } } } : reference;
            A.CallTo(() => combat.TryRestoreNativeReferenceStateAsync(world, obligation, A<NetworkCombatState>._, A<Func<bool>>._))
                .ReturnsLazily(() =>
                {
                    if (scenario == "revoked-during-restore") authorized = false;
                    return System.Threading.Tasks.Task.FromResult(actual);
                });
            var requested = RealTimeNativeCheckpointLimits.Copy(manifest);
            if (scenario == "wrong-reference") requested.CanonicalStateHash++;
            var restoration = client.RestoreLoadedNativeReferenceAsync(requested, reference, () => authorized);
            if (scenario == "success") Assert.That(await restoration, Is.EqualTo(manifest.CanonicalStateHash));
            else if (scenario == "revoked-during-restore") Assert.That(await restoration, Is.Null);
            else Assert.ThrowsAsync<InvalidOperationException>(async () => await restoration);
            if (scenario == "wrong-reference" || scenario == "unloaded" || scenario == "revoked")
                A.CallTo(() => combat.TryRestoreNativeReferenceStateAsync(A<object>._, A<object>._,
                    A<NetworkCombatState>._, A<Func<bool>>._)).MustNotHaveHappened();
            Assert.That(RealTimeNativeGraphRepair.Contains(world, obligation), Is.True);
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(client.Game.RealTimeCombat.BlocksSaving, Is.True);
            Assert.That(client.Game.RealTimeCombat.LastAuthoritativeSequence, Is.Zero);
            A.CallTo(() => NativeInteraction(client).SetPauseAsync(false)).MustNotHaveHappened();
            RealTimeNativeGraphRepair.Remove(world, obligation);
        }
    }
}
