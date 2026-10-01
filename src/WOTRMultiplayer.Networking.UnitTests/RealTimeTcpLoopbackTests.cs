using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using WOTRMultiplayer.Networking.Channels.TCP;
using WOTRMultiplayer.Networking.Configuration;
using WOTRMultiplayer.Networking.Messages;
using WOTRMultiplayer.Networking.Messages.Contracts;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Networking.UnitTests
{
    [TestFixture]
    public class RealTimeTcpLoopbackTests
    {
        [OneTimeSetUp]
        public void RegisterMessages()
        {
            NetworkMessages.Register(typeof(NotifyRealTimeModeTransition).Assembly);
        }

        [Test]
        public async Task DirectIpLoopback_RoundTripsModeOrderRepairAndHash()
        {
            var port = FindFreeLoopbackPort();
            var factory = new TcpFactory();
            var server = new NetworkServer(A.Fake<ILogger<NetworkServer>>(), factory);
            var client = new NetworkClient(A.Fake<ILogger<NetworkClient>>(), factory);
            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var receivedMode = new TaskCompletionSource<NotifyRealTimeModeTransition>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var receivedOrder = new TaskCompletionSource<NotifyRealTimeCombatCommandAccepted>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var receivedRepair = new TaskCompletionSource<NotifyRealTimeRepair>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var receivedDeath = new TaskCompletionSource<NotifyRealTimeUnitKilled>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var receivedHash = new TaskCompletionSource<ClientRealTimeStateHash>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnConnected = _ => connected.TrySetResult(true);
            server.OnClientConnected = _ => accepted.TrySetResult(true);
            client.OnMessageReceived = metadata =>
            {
                if (metadata.Message is NotifyRealTimeModeTransition mode)
                    receivedMode.TrySetResult(mode);
                if (metadata.Message is NotifyRealTimeCombatCommandAccepted order)
                    receivedOrder.TrySetResult(order);
                if (metadata.Message is NotifyRealTimeRepair repair)
                    receivedRepair.TrySetResult(repair);
                if (metadata.Message is NotifyRealTimeUnitKilled death)
                    receivedDeath.TrySetResult(death);
            };
            server.OnMessageReceived = metadata =>
            {
                if (metadata.Message is ClientRealTimeStateHash hash)
                    receivedHash.TrySetResult(hash);
            };

            try
            {
                server.Start(new NetworkServerConfiguration
                {
                    Host = IPAddress.Loopback.ToString(),
                    PortRangeStart = port,
                    PortRangeEnd = port
                });
                Assert.That(server.IsActive, Is.True);
                await client.ConnectAsync(IPAddress.Loopback.ToString(), port);
                Assert.That(await Task.WhenAny(connected.Task, Task.Delay(5000)), Is.SameAs(connected.Task));
                Assert.That(await Task.WhenAny(accepted.Task, Task.Delay(5000)), Is.SameAs(accepted.Task));

                server.Broadcast(new NotifyRealTimeModeTransition
                {
                    Epoch = 7,
                    Stage = (int)RealTimeModeTransitionStage.Commit,
                    Mode = 1,
                    CanonicalStateHash = 0x12345678UL,
                    State = new NetworkCombatState
                    {
                        Units =
                        [
                            new NetworkUnit
                            {
                                Id = "enemy",
                                Descriptor = new NetworkUnitDescriptor
                                {
                                    State = new NetworkUnitState { IsFinallyDead = true }
                                }
                            }
                        ]
                    }
                });
                Assert.That(await Task.WhenAny(receivedMode.Task, Task.Delay(5000)), Is.SameAs(receivedMode.Task));
                Assert.That(receivedMode.Task.Result.Epoch, Is.EqualTo(7));
                Assert.That(receivedMode.Task.Result.CanonicalStateHash, Is.EqualTo(0x12345678UL));
                Assert.That(receivedMode.Task.Result.State, Is.Not.Null);
                Assert.That(receivedMode.Task.Result.State.Units[0].Descriptor.State.IsFinallyDead, Is.True);

                server.Broadcast(new NotifyRealTimeCombatCommandAccepted
                {
                    Epoch = 7,
                    AuthoritativeSequence = 9,
                    PlayerId = 42,
                    ClientSequence = 3,
                    Kind = 1,
                    Movement = new NetworkUnitMoveTo
                    {
                        InitiatorUnitId = "guest-unit",
                        Destination = new NetworkVector3(1, 2, 3)
                    }
                });
                Assert.That(await Task.WhenAny(receivedOrder.Task, Task.Delay(5000)), Is.SameAs(receivedOrder.Task));
                Assert.That(receivedOrder.Task.Result.AuthoritativeSequence, Is.EqualTo(9));
                Assert.That(receivedOrder.Task.Result.Movement.InitiatorUnitId, Is.EqualTo("guest-unit"));

                server.Broadcast(new NotifyRealTimeRepair
                {
                    Epoch = 8,
                    Stage = (int)RealTimeRepairStage.Apply,
                    CanonicalStateHash = 0xAABBCCDDUL,
                    Mode = 1,
                    State = new NetworkCombatState()
                });
                Assert.That(await Task.WhenAny(receivedRepair.Task, Task.Delay(5000)), Is.SameAs(receivedRepair.Task));
                Assert.That(receivedRepair.Task.Result.Stage, Is.EqualTo((int)RealTimeRepairStage.Apply));
                Assert.That(receivedRepair.Task.Result.CanonicalStateHash, Is.EqualTo(0xAABBCCDDUL));

                server.Broadcast(new NotifyRealTimeUnitKilled
                {
                    Epoch = 8, Sequence = 10, UnitId = "enemy"
                });
                Assert.That(await Task.WhenAny(receivedDeath.Task, Task.Delay(5000)), Is.SameAs(receivedDeath.Task));
                Assert.That(receivedDeath.Task.Result.UnitId, Is.EqualTo("enemy"));
                Assert.That(receivedDeath.Task.Result.Sequence, Is.EqualTo(10));

                client.Broadcast(new ClientRealTimeStateHash
                {
                    Epoch = 7,
                    Sequence = 12,
                    CanonicalStateHash = 0x12345678UL,
                    Applied = true
                });
                Assert.That(await Task.WhenAny(receivedHash.Task, Task.Delay(5000)), Is.SameAs(receivedHash.Task));
                Assert.That(receivedHash.Task.Result.Sequence, Is.EqualTo(12));
                Assert.That(receivedHash.Task.Result.Applied, Is.True);
            }
            finally
            {
                client.Reset();
                server.Reset();
            }
        }

        private static int FindFreeLoopbackPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
