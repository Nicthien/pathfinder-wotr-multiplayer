using System;
using System.Linq;
using System.Threading.Tasks;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class RealTimeProtocolIntegrationTests
    {
        private static NotifyRealTimeCombatCommandAccepted SnapshotAcceptedMove(MultiplayerHost host, long sequence, float x = 1) => new()
        {
            SessionId = host.Game.Id, LoadedSaveSeed = host.Game.LoadedSaveSeed, Epoch = host.Game.RealTimeCombat.Epoch,
            AuthoritativeSequence = sequence, PlayerId = 42, ClientSequence = sequence, CommandId = $"42:{host.Game.RealTimeCombat.Epoch}:{sequence}",
            Kind = (int)RealTimeCommandKind.Move, Movement = new Networking.Messages.Contracts.NetworkUnitMoveTo
            { InitiatorUnitId = "hero", Destination = new Networking.Messages.Contracts.NetworkVector3(x, 0, 0) }
        };

        [TestCase("late")][TestCase("early")][TestCase("divergent-journal")][TestCase("bounded-history")]
        public void SnapshotReconcilesAcceptedEvidenceWithoutReplayingAnyCommand(string delivery)
        {
            if (!RealTimePresentationRuntime.Enabled) return;
            var (host, client, hostNetwork, network, queued, combat) = CreatePair();
            host.Game.RealTimeCombat.Mode = host.Game.RealTimeCombat.PreferredMode = CombatMode.RealTimeWithPause;
            client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            var accepted = SnapshotAcceptedMove(host, 1);
            var count = delivery == "bounded-history" ? 40 : 1;
            for (var i = 1; i <= count; i++) RealTimeAcceptedOrderLedger.Record(host.Game.RealTimeCombat, SnapshotAcceptedMove(host, i));
            host.Game.RealTimeCombat.LastAuthoritativeSequence = count;
            var receiveAccepted = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatCommandAccepted>(network);
            if (delivery == "early") receiveAccepted(NetworkConstants.HostPlayerId, accepted);
            if (delivery == "divergent-journal")
                RealTimeAcceptedOrderLedger.Record(client.Game.RealTimeCombat, SnapshotAcceptedMove(host, 1, 99));
            host.PublishRealTimeCombatSnapshot();
            var snapshot = ProtoBuf.Serializer.DeepClone((NotifyRealTimeCombatSnapshot)queued.Dequeue());
            Assert.That(snapshot.AcceptedOrders.LastIncludedSequence, Is.EqualTo(snapshot.Sequence));
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(network)(NetworkConstants.HostPlayerId, snapshot);
            var ack = (ClientRealTimeStateHash)queued.Dequeue();
            Assert.That(ack.Applied, Is.True);
            Assert.That(ack.CanonicalStateHash, Is.EqualTo(snapshot.CanonicalStateHash));
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeStateHash>(hostNetwork)(42, ack);
            receiveAccepted(NetworkConstants.HostPlayerId, accepted);
            receiveAccepted(NetworkConstants.HostPlayerId, accepted);
            RealTimeAcceptedOrderLedger.RequireUnchanged(client.Game.RealTimeCombat, snapshot.AcceptedOrders, client.Game.Id, client.Game.LoadedSaveSeed);
            Assert.That(client.Game.RealTimeCombat.AcceptedOrders.Count, Is.EqualTo(Math.Min(count, 32)));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(queued, Is.Empty);
            A.CallTo(() => combat.TryExecuteRealTimeCombatCommandAsync(A<RealTimeCommandKind>._,
                A<NetworkUnitMoveTo>._, A<NetworkUnitAttack>._, A<NetworkAbilityUse>._, A<Func<bool>>._)).MustNotHaveHappened();
            host.Reset();
        }

        [TestCase("missing")][TestCase("session")][TestCase("save")][TestCase("epoch")][TestCase("sequence")]
        [TestCase("changed-payload")][TestCase("changed-command-id")][TestCase("actual-world-mismatch")]
        public void SnapshotCannotCommitOrderEvidenceUntilBothScopeAndActualStateVerify(string fault)
        {
            if (!RealTimePresentationRuntime.Enabled) return;
            var (host, client, _, network, queued, combat) = CreatePair();
            host.Game.RealTimeCombat.Mode = host.Game.RealTimeCombat.PreferredMode = CombatMode.RealTimeWithPause;
            client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            RealTimeAcceptedOrderLedger.Record(host.Game.RealTimeCombat, SnapshotAcceptedMove(host, 1));
            host.Game.RealTimeCombat.LastAuthoritativeSequence = 1;
            var oldJournal = client.Game.RealTimeCombat.AcceptedOrders;
            host.PublishRealTimeCombatSnapshot(); var snapshot = (NotifyRealTimeCombatSnapshot)queued.Dequeue();
            switch (fault)
            {
                case "missing": snapshot.AcceptedOrders = null; break;
                case "session": snapshot.AcceptedOrders.SessionId = "other"; break;
                case "save": snapshot.AcceptedOrders.LoadedSaveSeed++; break;
                case "epoch": snapshot.AcceptedOrders.Epoch++; break;
                case "sequence": snapshot.AcceptedOrders.LastIncludedSequence++; break;
                case "changed-payload": snapshot.AcceptedOrders.Orders.Single().PayloadHash = Convert.ToBase64String(new byte[32]); break;
                case "changed-command-id": snapshot.AcceptedOrders.Orders.Single().CommandId = "42:0:999"; break;
                case "actual-world-mismatch":
                    A.CallTo(() => combat.TryApplyAndReadRealTimeCombatStateAsync(A<NetworkCombatState>._, A<Func<bool>>._))
                        .Returns(Task.FromResult(new NetworkCombatState { AreaEffects = new() { new() { Id = "unexpected" } } }));
                    break;
            }
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(network)(NetworkConstants.HostPlayerId, snapshot);
            var ack = (ClientRealTimeStateHash)queued.Dequeue();
            Assert.That(ack.Applied, Is.False);
            Assert.That(client.Game.RealTimeCombat.LastAuthoritativeSequence, Is.Zero);
            Assert.That(client.Game.RealTimeCombat.AcceptedOrders, Is.SameAs(oldJournal));
            if (fault != "actual-world-mismatch")
                A.CallTo(() => combat.TryApplyAndReadRealTimeCombatStateAsync(A<NetworkCombatState>._, A<Func<bool>>._)).MustNotHaveHappened();
            host.Reset();
        }
    }
}
