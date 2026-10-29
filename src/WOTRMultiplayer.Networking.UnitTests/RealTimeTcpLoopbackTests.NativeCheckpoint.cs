using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using WOTRMultiplayer.Networking.Channels.TCP;
using WOTRMultiplayer.Networking.Configuration;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Networking.UnitTests
{
    public partial class RealTimeTcpLoopbackTests
    {
        [Test]
        public async Task DirectIpLoopback_TransfersNativeArchiveWithPrepareFragmentsAndReceipt()
        {
            var port = FindFreeLoopbackPort(); var factory = new TcpFactory();
            var server = new NetworkServer(A.Fake<ILogger<NetworkServer>>(), factory);
            var client = new NetworkClient(A.Fake<ILogger<NetworkClient>>(), factory);
            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var prepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var complete = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var receipt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var referenceReceipt = new TaskCompletionSource<ClientRealTimeNativeCheckpointAck>(TaskCreationOptions.RunContinuationsAsynchronously);
            var bytes = RealTimeNativeCheckpointTransferTests.Bytes();
            var manifest = RealTimeNativeCheckpointTransferTests.Manifest(bytes);
            using var sender = new RealTimeNativeCheckpointSender(42, manifest, bytes);
            NotifyRealTimeNativeCheckpoint prepare = null;
            RealTimeNativeCheckpointReceiver receiver = null;
            client.OnConnected = _ => connected.TrySetResult(true);
            server.OnClientConnected = _ => accepted.TrySetResult(true);
            client.OnMessageReceived = metadata =>
            {
                try
                {
                    if (metadata.Message is NotifyRealTimeNativeCheckpoint header)
                    {
                        if (header.Stage == (int)RealTimeNativeCheckpointStage.Prepare)
                        {
                            prepare = header;
                            client.Broadcast(RealTimeNativeCheckpointTransferTests.Ack(header, RealTimeNativeCheckpointAckStage.Prepared));
                        }
                        else if (header.Stage == (int)RealTimeNativeCheckpointStage.Verify)
                        {
                            if (RealTimeNativeCheckpointLimits.ReferenceManifest(header) == null)
                                throw new InvalidOperationException("loopback-reference-header-refused");
                            var reference = RealTimeNativeReferenceCodec.Decode(header.ReferenceStateBytes);
                            if (reference.Units.Single().Id != new string('r', 70000))
                                throw new InvalidOperationException("loopback-reference-content-differs");
                            var ack = RealTimeNativeCheckpointTransferTests.Ack(manifest, RealTimeNativeCheckpointAckStage.ReferenceVerified);
                            ack.CanonicalStateHash = header.CanonicalStateHash;
                            ack.ReferenceStateHash = header.ReferenceStateHash;
                            client.Broadcast(ack); // Contract transport test, not a native world verification.
                        }
                        else receiver = new RealTimeNativeCheckpointReceiver(prepare, header);
                    }
                    if (metadata.Message is NotifyRealTimeNativeCheckpointChunk chunk)
                    {
                        if (receiver?.Accept(chunk) != true) throw new InvalidOperationException("loopback-chunk-refused");
                        client.Broadcast(RealTimeNativeCheckpointTransferTests.Ack(manifest, RealTimeNativeCheckpointAckStage.Chunk, chunk.Number));
                        if (receiver.IsComplete)
                        {
                            complete.TrySetResult(receiver.TakeCompleted());
                            client.Broadcast(RealTimeNativeCheckpointTransferTests.Ack(manifest, RealTimeNativeCheckpointAckStage.Received));
                        }
                    }
                }
                catch (Exception error) { complete.TrySetException(error); referenceReceipt.TrySetException(error); }
            };
            server.OnMessageReceived = metadata =>
            {
                if (metadata.Message is not ClientRealTimeNativeCheckpointAck ack) return;
                if (ack.Stage == (int)RealTimeNativeCheckpointAckStage.Prepared) prepared.TrySetResult(true);
                else if (ack.Stage == (int)RealTimeNativeCheckpointAckStage.ReferenceVerified) referenceReceipt.TrySetResult(ack);
                else if (sender.Acknowledge(42, ack) && sender.IsTransferred) receipt.TrySetResult(true);
            };
            try
            {
                server.Start(new NetworkServerConfiguration { Host = IPAddress.Loopback.ToString(), PortRangeStart = port, PortRangeEnd = port });
                await client.ConnectAsync(IPAddress.Loopback.ToString(), port);
                Assert.That(await Task.WhenAny(connected.Task, Task.Delay(5000)), Is.SameAs(connected.Task));
                Assert.That(await Task.WhenAny(accepted.Task, Task.Delay(5000)), Is.SameAs(accepted.Task));
                server.Broadcast(RealTimeNativeCheckpointTransferTests.Prepare());
                Assert.That(await Task.WhenAny(prepared.Task, Task.Delay(5000)), Is.SameAs(prepared.Task));
                server.Broadcast(manifest);
                foreach (var chunk in sender.GetDueChunks(DateTime.UtcNow).AsEnumerable().Reverse()) server.Broadcast(chunk);
                Assert.That(await Task.WhenAny(complete.Task, Task.Delay(5000)), Is.SameAs(complete.Task));
                Assert.That(await complete.Task, Is.EqualTo(bytes));
                Assert.That(await Task.WhenAny(receipt.Task, Task.Delay(5000)), Is.SameAs(receipt.Task));
                Assert.That(sender.IsTransferred, Is.True);
                var verify = RealTimeNativeCheckpointLimits.Copy(manifest);
                verify.Stage = (int)RealTimeNativeCheckpointStage.Verify;
                var reference = new WOTRMultiplayer.Networking.Messages.Contracts.NetworkCombatState();
                reference.Units.Add(new WOTRMultiplayer.Networking.Messages.Contracts.NetworkUnit { Id = new string('r', 70000) });
                verify.ReferenceStateBytes = RealTimeNativeReferenceCodec.Encode(reference);
                verify.ReferenceStateHash = RealTimeNativeCheckpointLimits.Hash(verify.ReferenceStateBytes);
                server.Broadcast(verify);
                Assert.That(await Task.WhenAny(referenceReceipt.Task, Task.Delay(5000)), Is.SameAs(referenceReceipt.Task));
                var verified = await referenceReceipt.Task;
                Assert.That(verified.ReferenceStateHash, Is.EqualTo(verify.ReferenceStateHash));
                Assert.That(verified.CanonicalStateHash, Is.EqualTo(manifest.CanonicalStateHash));
                Assert.That(verified.Number, Is.Zero);
            }
            finally { receiver?.Dispose(); client.Reset(); server.Reset(); }
        }
    }
}
