using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Abstractions.Hashing;
using WOTRMultiplayer.Abstractions.IO;
using WOTRMultiplayer.Abstractions.Random;
using WOTRMultiplayer.Abstractions.Settings;
using WOTRMultiplayer.Abstractions.Unity;
using WOTRMultiplayer.Config.Mapping;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Abstractions;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.UnitTests.FakeRules;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public partial class RealTimeProtocolIntegrationTests
    {
        [Test]
        public void TwoActors_SnapshotReply_UpdatesHostResponseTime()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            host.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            host.Game.RealTimeCombat.PreferredMode = CombatMode.RealTimeWithPause;
            client.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            host.PublishRealTimeCombatSnapshot();

            var snapshot = (NotifyRealTimeCombatSnapshot)queued.Dequeue();
            var sentAt = (ConcurrentDictionary<(long Epoch, long Sequence), DateTime>)typeof(MultiplayerHost)
                .GetField("_realTimeSnapshotSentAt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .GetValue(host);
            sentAt[(snapshot.Epoch, snapshot.Sequence)] = DateTime.UtcNow.AddMilliseconds(-50);
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(clientNetwork)(NetworkConstants.HostPlayerId, snapshot);
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeStateHash>(hostNetwork)(42,
                (ClientRealTimeStateHash)queued.Dequeue());

            Assert.That(host.Game.RealTimeCombat.ResponseTimeMs, Is.GreaterThanOrEqualTo(50));
            Assert.That(client.Game.RealTimeCombat.LastAuthoritativeSequence, Is.EqualTo(snapshot.Sequence));
            Assert.That(sentAt.ContainsKey((snapshot.Epoch, snapshot.Sequence)), Is.False);
        }

        [Test]
        public async Task TwoActors_ReorderedHostRolls_AreConsumedInRuleOrder()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(host.ResolveRealTimeDiceRoll("attack:d20", () => 12), Is.EqualTo(12));
            Assert.That(host.ResolveRealTimeDiceRoll("attack:d20", () => 18), Is.EqualTo(18));
            var first = (NotifyRealTimeRuleRoll)queued.Dequeue();
            var second = (NotifyRealTimeRuleRoll)queued.Dequeue();
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRuleRoll>(clientNetwork)
                (NetworkConstants.HostPlayerId, second);
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRuleRoll>(clientNetwork)
                (NetworkConstants.HostPlayerId, first);

            Assert.That(client.ResolveRealTimeDiceRoll("attack:d20", () => 12), Is.EqualTo(12));
            Assert.That(client.ResolveRealTimeDiceRoll("attack:d20", () => 18), Is.EqualTo(18));
            Assert.That(queued, Is.Empty);
        }

        [Test]
        public async Task TwoActors_DivergentLocalRoll_UsesHostResultAndStartsProtectedFallback()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(host.ResolveRealTimeDiceRoll("save:d20", () => 18), Is.EqualTo(18));
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRuleRoll>(clientNetwork)
                (NetworkConstants.HostPlayerId, (NotifyRealTimeRuleRoll)queued.Dequeue());
            Assert.That(client.ResolveRealTimeDiceRoll("save:d20", () => 7), Is.EqualTo(18));
            var fault = (ClientRealTimeRuleRollFault)queued.Dequeue();
            Assert.That(fault.Reason, Is.EqualTo("local-result-mismatch"));
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeRuleRollFault>(hostNetwork)(42, fault);
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Pump(queued, hostNetwork, clientNetwork);
            Assert.That(host.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
            Assert.That(client.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
        }

        [Test]
        public async Task TwoActors_MissingHostRoll_RequestsPausedRepair()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(client.ResolveRealTimeDiceRoll("save:d20", () => 11), Is.EqualTo(11));
            var fault = (ClientRealTimeRuleRollFault)queued.Dequeue();
            Assert.That(fault.Reason, Is.EqualTo("host-roll-timeout"));
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeRuleRollFault>(hostNetwork)(42, fault);

            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
            var prepare = (NotifyRealTimeRepair)queued.Dequeue();
            Assert.That(prepare.Stage, Is.EqualTo((int)RealTimeRepairStage.Prepare));
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRepair>(clientNetwork)
                (NetworkConstants.HostPlayerId, prepare);
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeRepairAck>(hostNetwork)
                (42, (ClientRealTimeRepairAck)queued.Dequeue());

            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.FallingBackToTurnBased));
            Assert.That(((NotifyRealTimeRepair)queued.Peek()).Stage,
                Is.EqualTo((int)RealTimeRepairStage.Fallback));
            Pump(queued, hostNetwork, clientNetwork);
            Assert.That(host.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
            Assert.That(client.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
        }

        [Test]
        public async Task TwoActors_HostAreaIdOverridesClientId()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(host.ResolveRealTimeEntityId("area:cloud", () => "host-id"), Is.EqualTo("host-id"));
            var published = (NotifyRealTimeEntityId)queued.Dequeue();
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeEntityId>(clientNetwork)
                (NetworkConstants.HostPlayerId, published);

            var localCalls = 0;
            Assert.That(client.ResolveRealTimeEntityId("area:cloud", () =>
            {
                localCalls++;
                return "client-id";
            }), Is.EqualTo("host-id"));
            Assert.That(localCalls, Is.EqualTo(1));
            Assert.That(queued, Is.Empty);
        }

        [Test]
        public async Task TwoActors_SpawnedUnitIdIgnoresForgedPeerAndUsesHost()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);

            var key = "unit:summoned-wolf:caster:position";
            Assert.That(host.ResolveRealTimeEntityId(key, () => "host-wolf-id"), Is.EqualTo("host-wolf-id"));
            var published = (NotifyRealTimeEntityId)queued.Dequeue();
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeEntityId>(clientNetwork)(42,
                new NotifyRealTimeEntityId
                {
                    Epoch = published.Epoch, Key = key, Sequence = published.Sequence,
                    EntityId = "forged-wolf-id"
                });
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeEntityId>(clientNetwork)
                (NetworkConstants.HostPlayerId, published);

            Assert.That(client.ResolveRealTimeEntityId(key, () => "client-wolf-id"),
                Is.EqualTo("host-wolf-id"));
            Assert.That(queued, Is.Empty);
        }

        [Test]
        public async Task TwoActors_MissingHostAreaId_UsesProtectedFallback()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(client.ResolveRealTimeEntityId("area:cloud", () => "client-id"), Is.EqualTo("client-id"));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            var fault = (ClientRealTimeEntityIdFault)queued.Dequeue();
            Assert.That(fault.Reason, Is.EqualTo("host-id-timeout"));
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeEntityIdFault>(hostNetwork)(42, fault);
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Pump(queued, hostNetwork, clientNetwork);
            Assert.That(host.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
            Assert.That(client.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
        }

        [Test]
        public async Task TwoActors_UnresolvableHostAIChoice_UsesProtectedFallback()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);

            client.ReportRealTimeAIActionFault("enemy", "spell", "target-not-found");
            var fault = (ClientRealTimeAIActionFault)queued.Dequeue();
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeAIActionFault>(hostNetwork)(42, fault);

            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
            Pump(queued, hostNetwork, clientNetwork);
            Assert.That(host.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
            Assert.That(client.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
        }

        [Test]
        public async Task TwoActors_RejectedAuthoritativeCommand_UsesProtectedFallback()
        {
            var (host, client, hostNetwork, clientNetwork, queued, clientCombat) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);
            A.CallTo(() => clientCombat.TryExecuteRealTimeCombatCommandAsync(A<RealTimeCommandKind>._,
                A<NetworkUnitMoveTo>._, A<NetworkUnitAttack>._, A<NetworkAbilityUse>._,
                A<Func<bool>>._)).Returns(Task.FromResult<bool?>(false));

            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatCommandAccepted>(clientNetwork)
                (NetworkConstants.HostPlayerId, new NotifyRealTimeCombatCommandAccepted
                {
                    SessionId = host.Game.Id,
                    LoadedSaveSeed = host.Game.LoadedSaveSeed,
                    Epoch = host.Game.RealTimeCombat.Epoch,
                    AuthoritativeSequence = 1,
                    // Presentation clients reject malformed host data, not commands
                    // that they deliberately never execute locally.
                    Kind = RealTimePresentationRuntime.Enabled ? -1 : (int)RealTimeCommandKind.Move,
                    Movement = new Networking.Messages.Contracts.NetworkUnitMoveTo
                    {
                        InitiatorUnitId = "hero",
                        Destination = new Networking.Messages.Contracts.NetworkVector3(1, 2, 3)
                    }
                });

            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Pump(queued, hostNetwork, clientNetwork);
            Assert.That(host.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
            Assert.That(client.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
        }

        [Test]
        public async Task TwoActors_HostOnlyFinalDeaths_AcceptReorderingButRejectDuplicates()
        {
            var (host, client, hostNetwork, clientNetwork, queued, clientCombat) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);
            A.CallTo(() => clientCombat.TryApplyRealTimeUnitDeathAsync(
                    A<string>._, A<NetworkPlayer>._, A<Func<bool>>._))
                .Returns(Task.FromResult<bool?>(true));

            client.OnUnitDeath("enemy", "enemy-group");
            Assert.That(queued, Is.Empty);
            host.OnUnitDeath("enemy", "enemy-group");
            if (RealTimePresentationRuntime.Enabled)
            {
                Assert.That(queued, Is.Empty, "The old death channel must not run with presentation replication.");
                A.CallTo(() => clientCombat.TryPresentRealTimeActionAsync(
                    A<NotifyRealTimeAuthoritativeEvent>._, A<Func<bool>>._)).Returns(Task.FromResult<bool?>(true));
                host.PublishRealTimeAuthoritativeEvent(UnitResult("enemy"));
                var firstResult = (NotifyRealTimeAuthoritativeEvent)queued.Dequeue();
                host.PublishRealTimeAuthoritativeEvent(UnitResult("second-enemy"));
                var secondResult = (NotifyRealTimeAuthoritativeEvent)queued.Dequeue();
                var receiveResult = FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeAuthoritativeEvent>(clientNetwork);
                receiveResult(NetworkConstants.HostPlayerId, secondResult);
                receiveResult(NetworkConstants.HostPlayerId, firstResult);
                receiveResult(NetworkConstants.HostPlayerId, firstResult);
                A.CallTo(() => clientCombat.TryPresentRealTimeActionAsync(
                    A<NotifyRealTimeAuthoritativeEvent>.That.Matches(frame => frame.UnitId == "enemy"),
                    A<Func<bool>>._)).MustHaveHappenedOnceExactly();
                A.CallTo(() => clientCombat.TryPresentRealTimeActionAsync(
                    A<NotifyRealTimeAuthoritativeEvent>.That.Matches(frame => frame.UnitId == "second-enemy"),
                    A<Func<bool>>._)).MustHaveHappenedOnceExactly();
                A.CallTo(() => clientCombat.TryApplyRealTimeUnitDeathAsync(
                    A<string>._, A<NetworkPlayer>._, A<Func<bool>>._)).MustNotHaveHappened();
                Assert.That(queued, Is.Empty);
                return;
            }
            var death = (NotifyRealTimeUnitKilled)queued.Dequeue();
            host.OnUnitDeath("second-enemy", "enemy-group");
            var secondDeath = (NotifyRealTimeUnitKilled)queued.Dequeue();
            Assert.That(death.Epoch, Is.EqualTo(host.Game.RealTimeCombat.Epoch));
            Assert.That(death.Sequence, Is.GreaterThan(0));
            Assert.That(secondDeath.Sequence, Is.GreaterThan(death.Sequence));
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeUnitKilled>(clientNetwork)(
                NetworkConstants.HostPlayerId, secondDeath);
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeUnitKilled>(clientNetwork)(
                NetworkConstants.HostPlayerId, death);
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeUnitKilled>(clientNetwork)(
                NetworkConstants.HostPlayerId, death);
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeUnitKilled>(clientNetwork)(42,
                new NotifyRealTimeUnitKilled
                {
                    Epoch = death.Epoch, Sequence = death.Sequence + 1, UnitId = "enemy"
                });

            A.CallTo(() => clientCombat.TryApplyRealTimeUnitDeathAsync(
                "enemy", A<NetworkPlayer>._, A<Func<bool>>._)).MustHaveHappenedOnceExactly();
            A.CallTo(() => clientCombat.TryApplyRealTimeUnitDeathAsync(
                "second-enemy", A<NetworkPlayer>._, A<Func<bool>>._)).MustHaveHappenedOnceExactly();
            Assert.That(queued, Is.Empty);
        }

        [Test]
        public async Task TwoActors_UnappliedHostDeath_EntersProtectedFallback()
        {
            var (host, client, hostNetwork, clientNetwork, queued, clientCombat) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);
            A.CallTo(() => clientCombat.TryApplyRealTimeUnitDeathAsync(
                    "enemy", A<NetworkPlayer>._, A<Func<bool>>._))
                .Returns(Task.FromResult<bool?>(false));

            if (RealTimePresentationRuntime.Enabled)
            {
                A.CallTo(() => clientCombat.TryPresentRealTimeActionAsync(
                    A<NotifyRealTimeAuthoritativeEvent>._, A<Func<bool>>._)).Returns(Task.FromResult<bool?>(false));
                host.PublishRealTimeAuthoritativeEvent(UnitResult("enemy"));
                FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeAuthoritativeEvent>(clientNetwork)(
                    NetworkConstants.HostPlayerId, (NotifyRealTimeAuthoritativeEvent)queued.Dequeue());
            }
            else
            {
                host.OnUnitDeath("enemy", "enemy-group");
                FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeUnitKilled>(clientNetwork)(
                    NetworkConstants.HostPlayerId, (NotifyRealTimeUnitKilled)queued.Dequeue());
            }
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            var fault = (ClientRealTimeCommandFault)queued.Dequeue();
            Assert.That(fault.Reason, Is.EqualTo(RealTimePresentationRuntime.Enabled
                ? "action-presentation-not-applied" : "host-unit-death-not-applied"));
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeCommandFault>(hostNetwork)(42, fault);
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(host.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
            Assert.That(client.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
        }

        private static NotifyRealTimeAuthoritativeEvent UnitResult(string unitId) => new()
        {
            UnitId = unitId, ActionId = "result:" + unitId, Kind = (int)RealTimeAuthoritativeEventKind.UnitResult,
            UnitResult = new RealTimeUnitResultPayload
            {
                Reason = "life-state",
                State = new Networking.Messages.Contracts.NetworkUnitDescriptor
                {
                    Damage = 40, ResourcesCaptured = true, SpellbooksCaptured = true,
                    Stats = new Networking.Messages.Contracts.NetworkCharacterStats(),
                    State = new Networking.Messages.Contracts.NetworkUnitState { IsFinallyDead = true },
                    Vitals = new Networking.Messages.Contracts.NetworkUnitVitals
                    {
                        Damage = 40, LifeState = 2, IsFinallyDead = true,
                        HitPoints = new Networking.Messages.Contracts.NetworkStatValues
                            { BaseValue = 30, ModifiedValue = 30, ModifiedValueRaw = 30, PermanentValue = 30 },
                        TemporaryHitPoints = new Networking.Messages.Contracts.NetworkStatValues(),
                        NonLethal = new Networking.Messages.Contracts.NetworkStatValues()
                    }
                }
            }
        };

        [Test]
        public async Task TwoActors_DelayedDeathDuringModePrepare_UsesOnlySelectedReplicationPath()
        {
            var (host, client, hostNetwork, clientNetwork, queued, clientCombat) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);
            A.CallTo(() => clientCombat.TryApplyRealTimeUnitDeathAsync(
                    "enemy", A<NetworkPlayer>._, A<Func<bool>>._))
                .Returns(Task.FromResult<bool?>(true));

            host.OnUnitDeath("enemy", "enemy-group");
            if (RealTimePresentationRuntime.Enabled)
            {
                Assert.That(queued, Is.Empty);
                host.PublishRealTimeAuthoritativeEvent(UnitResult("enemy"));
            }
            var delayedDeath = queued.Dequeue();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.TurnBased), Is.True);
            var prepare = (NotifyRealTimeModeTransition)queued.Dequeue();
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeModeTransition>(clientNetwork)(
                NetworkConstants.HostPlayerId, prepare);
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.PreparingModeSwitch));
            if (RealTimePresentationRuntime.Enabled)
                FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeAuthoritativeEvent>(clientNetwork)(
                    NetworkConstants.HostPlayerId, (NotifyRealTimeAuthoritativeEvent)delayedDeath);
            else
                FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeUnitKilled>(clientNetwork)(
                    NetworkConstants.HostPlayerId, (NotifyRealTimeUnitKilled)delayedDeath);
            Pump(queued, hostNetwork, clientNetwork);

            A.CallTo(() => clientCombat.TryApplyRealTimeUnitDeathAsync(
                "enemy", A<NetworkPlayer>._, A<Func<bool>>._))
                .MustHaveHappened(RealTimePresentationRuntime.Enabled ? 0 : 1, Times.Exactly);
            if (RealTimePresentationRuntime.Enabled)
                A.CallTo(() => clientCombat.TryPresentRealTimeActionAsync(
                    A<NotifyRealTimeAuthoritativeEvent>._, A<Func<bool>>._)).MustNotHaveHappened();
            Assert.That(host.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
            Assert.That(client.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
        }

        [Test]
        public async Task TwoActors_HostNoActionChoiceArrivesOnceWithoutClientDecision()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(client.TryConsumeAuthoritativeRealTimeAIAction("enemy", out _), Is.False);
            host.OnAfterAISelectedAction(new NetworkAIAction { UnitId = "enemy" });
            var choice = (NotifyAIActionSelected)queued.Dequeue();
            FakeUtils.GetNetworkReceiverHandler<NotifyAIActionSelected>(clientNetwork)
                (NetworkConstants.HostPlayerId, choice);
            FakeUtils.GetNetworkReceiverHandler<NotifyAIActionSelected>(clientNetwork)
                (NetworkConstants.HostPlayerId, choice);

            Assert.That(client.TryConsumeAuthoritativeRealTimeAIAction("enemy", out var selected), Is.True);
            Assert.That(selected.Id, Is.Null);
            Assert.That(client.TryConsumeAuthoritativeRealTimeAIAction("enemy", out _), Is.False);
        }

        [Test]
        public async Task Client_IgnoresRealTimeAuthorityMessagesFromNonHost()
        {
            var (host, client, hostNetwork, clientNetwork, queued, clientCombat) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);
            var epoch = client.Game.RealTimeCombat.Epoch;
            var preferredMode = client.Game.RealTimeCombat.PreferredMode;
            var preferenceSequence = client.Game.RealTimeCombat.PreferenceSequence;
            const long forgedSender = 42;

            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(clientNetwork)(forgedSender,
                new NotifyRealTimeCombatSnapshot
                {
                    Epoch = epoch, Sequence = 1,
                    State = new WOTRMultiplayer.Networking.Messages.Contracts.NetworkCombatState()
                });
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatCommandAccepted>(clientNetwork)(forgedSender,
                new NotifyRealTimeCombatCommandAccepted
                {
                    Epoch = epoch, AuthoritativeSequence = 1, Kind = (int)RealTimeCommandKind.Move,
                    Movement = new WOTRMultiplayer.Networking.Messages.Contracts.NetworkUnitMoveTo
                    {
                        InitiatorUnitId = "guest-unit"
                    }
                });
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimePauseChanged>(clientNetwork)(forgedSender,
                new NotifyRealTimePauseChanged
                {
                    Epoch = epoch, Sequence = 1, IsPaused = true,
                    Policy = (int)PausePolicy.HostOnly, PausedPlayers = [forgedSender]
                });
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatPreference>(clientNetwork)(forgedSender,
                new NotifyRealTimeCombatPreference
                {
                    Sequence = client.Game.RealTimeCombat.PreferenceSequence + 1,
                    PreferredMode = (int)CombatMode.RealTimeWithPause,
                    PausePolicy = (int)PausePolicy.AnyoneFreeResume
                });
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeModeTransition>(clientNetwork)(forgedSender,
                new NotifyRealTimeModeTransition
                {
                    Epoch = epoch + 1, Stage = (int)RealTimeModeTransitionStage.Prepare,
                    Mode = (int)CombatMode.TurnBased
                });
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRepair>(clientNetwork)(forgedSender,
                new NotifyRealTimeRepair
                {
                    Epoch = epoch + 1, Stage = (int)RealTimeRepairStage.Prepare,
                    Mode = (int)CombatMode.RealTimeWithPause
                });

            Assert.That(queued, Is.Empty);
            Assert.That(client.Game.RealTimeCombat.Epoch, Is.EqualTo(epoch));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(client.Game.RealTimeCombat.PauseSequence, Is.Zero);
            Assert.That(client.Game.RealTimeCombat.PreferredMode, Is.EqualTo(preferredMode));
            Assert.That(client.Game.RealTimeCombat.PreferenceSequence, Is.EqualTo(preferenceSequence));
            Assert.That(client.Game.RealTimeCombat.LastAuthoritativeSequence, Is.Zero);
            A.CallTo(() => clientCombat.TryExecuteRealTimeCombatCommandAsync(A<RealTimeCommandKind>._,
                A<NetworkUnitMoveTo>._, A<NetworkUnitAttack>._, A<NetworkAbilityUse>._, A<Func<bool>>._))
                .MustNotHaveHappened();
            A.CallTo(() => clientCombat.TryApplyAndReadRealTimeCombatStateAsync(
                A<NetworkCombatState>._, A<Func<bool>>._))
                .MustHaveHappenedOnceExactly(); // Legitimate mode commit only.
        }

        [Test]
        public void Host_UnansweredSnapshot_AgesOutAsMissingApplicationReply()
        {
            var (host, _, _, _, queued, _) = CreatePair();
            host.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            host.Game.RealTimeCombat.PreferredMode = CombatMode.RealTimeWithPause;
            var nextSnapshot = typeof(MultiplayerHost).GetField("_nextRealTimeSnapshotAt",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            for (var i = 0; i < 34; i++)
            {
                nextSnapshot.SetValue(host, DateTime.MinValue);
                host.PublishRealTimeCombatSnapshot();
            }

            Assert.That(host.Game.RealTimeCombat.MissingSnapshotReplies, Is.EqualTo(1));
            var latest = (NotifyRealTimeCombatSnapshot)queued.ToArray()[33];
            Assert.That(latest.MissingReplies, Is.EqualTo(1));
        }

        [Test]
        public void Host_ThreeExpiredReplies_PausesAndStartsAutomaticVerification()
        {
            var (host, _, _, _, queued, _) = CreatePair();
            host.Game.RealTimeCombat.Mode = CombatMode.RealTimeWithPause;
            host.Game.RealTimeCombat.PreferredMode = CombatMode.RealTimeWithPause;
            var nextSnapshot = typeof(MultiplayerHost).GetField("_nextRealTimeSnapshotAt",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            for (var i = 0; i < 36; i++)
            {
                nextSnapshot.SetValue(host, DateTime.MinValue);
                host.PublishRealTimeCombatSnapshot();
            }

            Assert.That(host.Game.RealTimeCombat.MissingSnapshotReplies, Is.EqualTo(3));
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
            Assert.That(queued.ToArray()[36], Is.TypeOf<NotifyRealTimeRepair>());
        }

        [Test]
        public async Task TwoActors_LostAndDuplicatePrepare_ConvergeOnRealTime()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);

            // Drop the first Prepare, then trigger the host's application-level retry.
            Assert.That(queued.Dequeue(), Is.TypeOf<NotifyRealTimeModeTransition>());
            typeof(MultiplayerHost).GetField("_realTimeModeSwitchDeadline",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(host, DateTime.UtcNow.AddSeconds(-1));
            host.PublishRealTimeCombatSnapshot();

            var prepare = (NotifyRealTimeModeTransition)queued.Dequeue();
            DeliverToClient(clientNetwork, prepare);
            DeliverToClient(clientNetwork, prepare); // Duplicate arrives before either ACK reaches the host.
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(host.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.RealTimeWithPause));
            Assert.That(client.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.RealTimeWithPause));
            Assert.That(host.Game.RealTimeCombat.CanonicalStateHash, Is.EqualTo(client.Game.RealTimeCombat.CanonicalStateHash));
        }

        [Test]
        public async Task TwoActors_JitterAndDuplicateModePackets_ConvergeAcrossDeterministicSeeds()
        {
            for (var seed = 0; seed < 12; seed++)
            {
                var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
                Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause),
                    Is.True, $"seed={seed}");
                var random = new System.Random(seed);
                var delayed = new List<(object Message, int Due)>();
                for (var tick = 0; tick < 150; tick++)
                {
                    while (queued.Count > 0)
                    {
                        var message = queued.Dequeue();
                        delayed.Add((message, tick + random.Next(4)));
                        if (random.Next(3) == 0)
                            delayed.Add((message, tick + random.Next(4)));
                    }
                    var ready = delayed.Where(x => x.Due <= tick).ToList();
                    if (ready.Count > 0)
                    {
                        var chosen = ready[random.Next(ready.Count)];
                        delayed.Remove(chosen);
                        switch (chosen.Message)
                        {
                            case NotifyRealTimeModeTransition transition:
                                DeliverToClient(clientNetwork, transition);
                                break;
                            case ClientRealTimeModeTransitionAck ack:
                                FakeUtils.GetNetworkReceiverHandler<ClientRealTimeModeTransitionAck>(hostNetwork)(42, ack);
                                break;
                            default:
                                Assert.Fail($"Unexpected mode packet: {chosen.Message.GetType().Name}");
                                break;
                        }
                    }
                    if (queued.Count == 0 && delayed.Count == 0
                        && host.Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Stable
                        && client.Game.RealTimeCombat.Phase == CombatSynchronizationPhase.Stable)
                        break;
                }

                Assert.That(queued, Is.Empty, $"seed={seed}");
                Assert.That(delayed, Is.Empty, $"seed={seed}");
                Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable), $"seed={seed}");
                Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable), $"seed={seed}");
                Assert.That(host.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.RealTimeWithPause), $"seed={seed}");
                Assert.That(client.Game.RealTimeCombat.Mode, Is.EqualTo(host.Game.RealTimeCombat.Mode), $"seed={seed}");
                Assert.That(client.Game.RealTimeCombat.Epoch, Is.EqualTo(host.Game.RealTimeCombat.Epoch), $"seed={seed}");
            }
        }

        [Test]
        public async Task TwoActors_SimultaneousMovesAndAttack_KeepAuthoritativeOrder()
        {
            var (host, client, hostNetwork, clientNetwork, queued, clientCombat) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);
            var hostPlayer = host.Game.Players.Find(player => player.Id == NetworkConstants.HostPlayerId);
            var guestPlayer = host.Game.Players.Find(player => player.Id == 42);
            host.Game.Characters.Add(new NetworkCharacter { UnitId = "host-unit", Owner = hostPlayer });
            host.Game.Characters.Add(new NetworkCharacter { UnitId = "guest-unit", Owner = guestPlayer });
            client.Game.Characters.Add(new NetworkCharacter { UnitId = "guest-unit", Owner = guestPlayer });

            Assert.That(host.PublishLocalRealTimeCombatCommand(RealTimeCommandKind.Move,
                movement: new NetworkUnitMoveTo { InitiatorUnitId = "host-unit", Destination = new NetworkVector3(1, 0, 0) }), Is.True);
            Assert.That(client.SubmitRealTimeCombatCommand(RealTimeCommandKind.Move,
                movement: new NetworkUnitMoveTo { InitiatorUnitId = "guest-unit", Destination = new NetworkVector3(2, 0, 0) }), Is.True);
            Assert.That(client.SubmitRealTimeCombatCommand(RealTimeCommandKind.Attack,
                attack: new NetworkUnitAttack { InitiatorUnitId = "guest-unit", TargetUnitId = "host-unit" }), Is.True);
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(host.Game.RealTimeCombat.LastAuthoritativeSequence, Is.EqualTo(3));
            Assert.That(client.Game.RealTimeCombat.LastAuthoritativeSequence, Is.EqualTo(3));
            if (RealTimePresentationRuntime.Enabled)
                A.CallTo(() => clientCombat.TryExecuteRealTimeCombatCommandAsync(A<RealTimeCommandKind>._,
                    A<NetworkUnitMoveTo>._, A<NetworkUnitAttack>._, A<NetworkAbilityUse>._,
                    A<Func<bool>>._)).MustNotHaveHappened();
            else
            {
                A.CallTo(() => clientCombat.TryExecuteRealTimeCombatCommandAsync(RealTimeCommandKind.Move,
                    A<NetworkUnitMoveTo>._, null, null, A<Func<bool>>._)).MustHaveHappenedTwiceExactly();
                A.CallTo(() => clientCombat.TryExecuteRealTimeCombatCommandAsync(RealTimeCommandKind.Attack,
                    null, A<NetworkUnitAttack>._, null, A<Func<bool>>._)).MustHaveHappenedOnceExactly();
            }
            host.PublishRealTimeCombatSnapshot();
            Pump(queued, hostNetwork, clientNetwork);
            Assert.That(host.Game.RealTimeCombat.CanonicalStateHash,
                Is.EqualTo(client.Game.RealTimeCombat.CanonicalStateHash));
        }

        [Test]
        public async Task TwoActors_LostAndDuplicateCommit_ConvergeOnRealTime()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            DeliverToClient(clientNetwork, (NotifyRealTimeModeTransition)queued.Dequeue());
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeModeTransitionAck>(hostNetwork)(42,
                (ClientRealTimeModeTransitionAck)queued.Dequeue());

            Assert.That(((NotifyRealTimeModeTransition)queued.Dequeue()).Stage,
                Is.EqualTo((int)RealTimeModeTransitionStage.Commit)); // Drop the first Commit.
            typeof(MultiplayerHost).GetField("_realTimeModeSwitchDeadline",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(host, DateTime.UtcNow.AddSeconds(-1));
            host.PublishRealTimeCombatSnapshot();

            var commit = (NotifyRealTimeModeTransition)queued.Dequeue();
            DeliverToClient(clientNetwork, commit);
            DeliverToClient(clientNetwork, commit);
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(host.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.RealTimeWithPause));
            Assert.That(client.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.RealTimeWithPause));
        }

        [Test]
        public async Task TwoActors_LostAndDuplicateComplete_HostWaitsUntilClientAcknowledges()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            DeliverToClient(clientNetwork, (NotifyRealTimeModeTransition)queued.Dequeue());
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeModeTransitionAck>(hostNetwork)(42,
                (ClientRealTimeModeTransitionAck)queued.Dequeue());
            DeliverToClient(clientNetwork, (NotifyRealTimeModeTransition)queued.Dequeue());
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeModeTransitionAck>(hostNetwork)(42,
                (ClientRealTimeModeTransitionAck)queued.Dequeue());

            Assert.That(((NotifyRealTimeModeTransition)queued.Dequeue()).Stage,
                Is.EqualTo((int)RealTimeModeTransitionStage.Complete)); // Drop first Complete.
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Synchronizing));
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
            typeof(MultiplayerHost).GetField("_realTimeModeSwitchDeadline",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(host, DateTime.UtcNow.AddSeconds(-1));
            host.PublishRealTimeCombatSnapshot();

            var complete = (NotifyRealTimeModeTransition)queued.Dequeue();
            DeliverToClient(clientNetwork, complete);
            DeliverToClient(clientNetwork, complete);
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(host.Game.RealTimeCombat.Mode, Is.EqualTo(client.Game.RealTimeCombat.Mode));
        }

        [Test]
        public async Task TwoActors_DelayedOldEpochPackets_CannotReenterRealTimeAfterFallback()
        {
            var (host, client, hostNetwork, clientNetwork, queued, clientCombat) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            var oldPrepare = (NotifyRealTimeModeTransition)queued.Peek();
            Pump(queued, hostNetwork, clientNetwork);
            Assert.That(client.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.RealTimeWithPause));

            host.PublishRealTimeCombatSnapshot();
            var oldSnapshot = (NotifyRealTimeCombatSnapshot)queued.Dequeue();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.TurnBased), Is.True);
            Pump(queued, hostNetwork, clientNetwork);
            Assert.That(host.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
            Assert.That(client.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
            var settledEpoch = client.Game.RealTimeCombat.Epoch;
            var settledHash = client.Game.RealTimeCombat.CanonicalStateHash;

            // Reordering and duplication can deliver old packets long after the next barrier.
            DeliverToClient(clientNetwork, oldPrepare);
            DeliverToClient(clientNetwork, oldPrepare);
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(clientNetwork)
                (NetworkConstants.HostPlayerId, oldSnapshot);

            Assert.That(queued, Is.Empty);
            Assert.That(client.Game.RealTimeCombat.Epoch, Is.EqualTo(settledEpoch));
            Assert.That(client.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(client.Game.RealTimeCombat.CanonicalStateHash, Is.EqualTo(settledHash));
            A.CallTo(() => clientCombat.TryApplyAndReadRealTimeCombatStateAsync(
                A<NetworkCombatState>._, A<Func<bool>>._))
                .MustHaveHappenedTwiceExactly(); // One checkpoint for each legitimate mode commit.
        }

        [Test]
        public async Task TwoActors_CombatMessagesDuringModeBarrier_DoNotMutateClient()
        {
            var (host, client, hostNetwork, clientNetwork, queued, clientCombat) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            DeliverToClient(clientNetwork, (NotifyRealTimeModeTransition)queued.Dequeue());
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeModeTransitionAck>(hostNetwork)(42,
                (ClientRealTimeModeTransitionAck)queued.Dequeue());
            DeliverToClient(clientNetwork, (NotifyRealTimeModeTransition)queued.Dequeue());

            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Synchronizing));
            var epoch = client.Game.RealTimeCombat.Epoch;
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(clientNetwork)(
                NetworkConstants.HostPlayerId, new NotifyRealTimeCombatSnapshot
            {
                Epoch = epoch,
                Sequence = 1,
                State = new WOTRMultiplayer.Networking.Messages.Contracts.NetworkCombatState()
            });
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatCommandAccepted>(clientNetwork)(
                NetworkConstants.HostPlayerId, new NotifyRealTimeCombatCommandAccepted
            {
                Epoch = epoch,
                AuthoritativeSequence = 1,
                Kind = (int)RealTimeCommandKind.Move,
                Movement = new WOTRMultiplayer.Networking.Messages.Contracts.NetworkUnitMoveTo
                {
                    InitiatorUnitId = "guest-unit"
                }
            });

            A.CallTo(() => clientCombat.TryApplyAndReadRealTimeCombatStateAsync(
                A<NetworkCombatState>._, A<Func<bool>>._))
                .MustHaveHappenedOnceExactly(); // Commit only; not the reordered moving snapshot.
            A.CallTo(() => clientCombat.TryExecuteRealTimeCombatCommandAsync(A<RealTimeCommandKind>._,
                A<NetworkUnitMoveTo>._, A<NetworkUnitAttack>._, A<NetworkAbilityUse>._, A<Func<bool>>._))
                .MustNotHaveHappened();
            Assert.That(client.Game.RealTimeCombat.LastAuthoritativeSequence, Is.Zero);

            Pump(queued, hostNetwork, clientNetwork);
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
        }

        [Test]
        public async Task TwoActors_LostAbortComplete_RemainsPausedUntilAcknowledged()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            DeliverToClient(clientNetwork, (NotifyRealTimeModeTransition)queued.Dequeue());
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeModeTransitionAck>(hostNetwork)(42,
                (ClientRealTimeModeTransitionAck)queued.Dequeue());
            var commit = (NotifyRealTimeModeTransition)queued.Dequeue();
            commit.CanonicalStateHash ^= 1UL; // Force the client to reject the checkpoint.
            DeliverToClient(clientNetwork, commit);
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeModeTransitionAck>(hostNetwork)(42,
                (ClientRealTimeModeTransitionAck)queued.Dequeue());
            Assert.That(((NotifyRealTimeModeTransition)queued.Peek()).Stage,
                Is.EqualTo((int)RealTimeModeTransitionStage.Abort));
            PumpUntilComplete(queued, hostNetwork, clientNetwork);

            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
            Assert.That(((NotifyRealTimeModeTransition)queued.Dequeue()).Stage,
                Is.EqualTo((int)RealTimeModeTransitionStage.Complete)); // Drop it.
            typeof(MultiplayerHost).GetField("_realTimeModeSwitchDeadline",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(host, DateTime.UtcNow.AddSeconds(-1));
            host.PublishRealTimeCombatSnapshot();
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(host.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
            Assert.That(client.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.TurnBased));
        }

        [Test]
        public async Task Host_PeerDisconnectsWhileCompleting_LeavesBarrierInSoloPlay()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            DeliverToClient(clientNetwork, (NotifyRealTimeModeTransition)queued.Dequeue());
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeModeTransitionAck>(hostNetwork)(42,
                (ClientRealTimeModeTransitionAck)queued.Dequeue());
            DeliverToClient(clientNetwork, (NotifyRealTimeModeTransition)queued.Dequeue());
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeModeTransitionAck>(hostNetwork)(42,
                (ClientRealTimeModeTransitionAck)queued.Dequeue());
            queued.Dequeue(); // Final Complete cannot be delivered after the peer disconnects.

            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Synchronizing));
            host.Game.Players.RemoveAll(player => player.Id == 42);
            typeof(MultiplayerHost).GetField("_realTimeModeSwitchDeadline",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(host, DateTime.UtcNow.AddSeconds(-1));
            host.PublishRealTimeCombatSnapshot();

            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(host.Game.RealTimeCombat.Mode, Is.EqualTo(CombatMode.RealTimeWithPause));
        }

        [Test]
        public async Task TwoActors_LostRepairPrepare_ResynchronizeAndResume()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));

            await host.BeginRealTimeRepairAsync();
            Assert.That(((NotifyRealTimeRepair)queued.Dequeue()).Stage,
                Is.EqualTo((int)RealTimeRepairStage.Prepare)); // Drop first Prepare.
            typeof(MultiplayerHost).GetField("_realTimeRepairDeadline",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(host, DateTime.UtcNow.AddSeconds(-1));
            host.PublishRealTimeCombatSnapshot();
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(host.Game.RealTimeCombat.Epoch, Is.EqualTo(client.Game.RealTimeCombat.Epoch));
        }

        [Test]
        public async Task TwoActors_TemporalMismatchDuringVerify_RequestsFullApplyEvenWhenHashMatches()
        {
            var (host, _, hostNetwork, clientNetwork, queued, _) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);

            await host.BeginRealTimeRepairAsync();
            var prepare = (NotifyRealTimeRepair)queued.Dequeue();
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRepair>(clientNetwork)
                (NetworkConstants.HostPlayerId, prepare);
            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeRepairAck>(hostNetwork)
                (42, (ClientRealTimeRepairAck)queued.Dequeue());
            var verify = (NotifyRealTimeRepair)queued.Dequeue();
            Assert.That(verify.Stage, Is.EqualTo((int)RealTimeRepairStage.Verify));
            Assert.That(verify.State, Is.Not.Null);

            FakeUtils.GetNetworkReceiverHandler<ClientRealTimeRepairAck>(hostNetwork)(42,
                new ClientRealTimeRepairAck
                {
                    Epoch = verify.Epoch, Stage = verify.Stage, PlayerId = 42,
                    CanonicalStateHash = verify.CanonicalStateHash,
                    Applied = true, TemporalMismatch = true
                });

            Assert.That(((NotifyRealTimeRepair)queued.Peek()).Stage,
                Is.EqualTo((int)RealTimeRepairStage.Apply));
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
        }

        [Test]
        public async Task TwoActors_LostAndDuplicateRepairResume_HostWaitsForAcknowledgement()
        {
            var (host, client, hostNetwork, clientNetwork, queued, _) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);
            await host.BeginRealTimeRepairAsync();
            PumpUntilRepairResume(queued, hostNetwork, clientNetwork);

            Assert.That(((NotifyRealTimeRepair)queued.Dequeue()).Stage,
                Is.EqualTo((int)RealTimeRepairStage.Resume)); // Drop first Resume.
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Repairing));
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
            typeof(MultiplayerHost).GetField("_realTimeRepairDeadline",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(host, DateTime.UtcNow.AddSeconds(-1));
            host.PublishRealTimeCombatSnapshot();

            var resume = (NotifyRealTimeRepair)queued.Dequeue();
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRepair>(clientNetwork)(NetworkConstants.HostPlayerId, resume);
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRepair>(clientNetwork)(NetworkConstants.HostPlayerId, resume);
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
        }

        [Test]
        public async Task TwoActors_DivergentUnit_WithMockedSuccessfulApply_Resumes()
        {
            var (host, client, hostNetwork, clientNetwork, queued, clientCombat) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);
            var calls = 0;
            A.CallTo(() => clientCombat.GetRealTimeCombatStateAsync()).ReturnsLazily(() =>
                Task.FromResult(++calls == 1
                    ? new NetworkCombatState { Units = [new NetworkUnit { Id = "divergent-unit" }] }
                    : new NetworkCombatState()));

            await host.BeginRealTimeRepairAsync();
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(calls, Is.EqualTo(1)); // Verify samples once; Apply+read is one guarded dispatch.
            A.CallTo(() => clientCombat.TryApplyAndReadRealTimeCombatStateAsync(
                A<NetworkCombatState>._, A<Func<bool>>._)).MustHaveHappenedTwiceExactly();
            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.Stable));
            Assert.That(host.Game.RealTimeCombat.CanonicalStateHash, Is.EqualTo(client.Game.RealTimeCombat.CanonicalStateHash));
        }

        [Test]
        public async Task TwoActors_UnrepairableUnit_StaysPausedAndBlocksSaving()
        {
            var (host, client, hostNetwork, clientNetwork, queued, clientCombat) = CreatePair();
            Assert.That(await host.RequestRealTimeCombatModeAsync(CombatMode.RealTimeWithPause), Is.True);
            Pump(queued, hostNetwork, clientNetwork);
            A.CallTo(() => clientCombat.GetRealTimeCombatStateAsync()).Returns(
                Task.FromResult(new NetworkCombatState { Units = [new NetworkUnit { Id = "unrepairable-unit" }] }));
            A.CallTo(() => clientCombat.TryApplyAndReadRealTimeCombatStateAsync(
                A<NetworkCombatState>._, A<Func<bool>>._)).Returns(
                Task.FromResult(new NetworkCombatState { Units = [new NetworkUnit { Id = "unrepairable-unit" }] }));

            await host.BeginRealTimeRepairAsync();
            Pump(queued, hostNetwork, clientNetwork);

            Assert.That(host.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.FallingBackToTurnBased));
            Assert.That(client.Game.RealTimeCombat.Phase, Is.EqualTo(CombatSynchronizationPhase.FallingBackToTurnBased));
            Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
            Assert.That(client.IsSavingBlockedByCombatSync, Is.True);
        }

        private static (MultiplayerHost Host, MultiplayerClient Client, INetworkHostConnection HostNetwork,
            INetworkClientConnection ClientNetwork, Queue<object> Queued, ICombatInteractionService ClientCombat) CreatePair(
                NetworkCombatState endState = null, Queue<Action> mainThreadQueue = null)
        {
            var mapper = new MapperConfiguration(x =>
            {
                x.AddProfile<NetworkingProfile>();
                x.AddProfile<NetworkMessagesProfile>();
            }).CreateMapper();
            var hostGame = A.Fake<IGameInteractionService>();
            var clientGame = A.Fake<IGameInteractionService>();
            A.CallTo(() => hostGame.TrySetTurnBasedCombatModeAsync(A<bool>._, A<Func<bool>>._))
                .ReturnsLazily((bool _, Func<bool> canApply) => Task.FromResult(canApply()));
            A.CallTo(() => clientGame.TrySetTurnBasedCombatModeAsync(A<bool>._, A<Func<bool>>._))
                .ReturnsLazily((bool _, Func<bool> canApply) => Task.FromResult(canApply()));
            var hostCombat = A.Fake<ICombatInteractionService>();
            var clientCombat = A.Fake<ICombatInteractionService>();
            A.CallTo(() => clientCombat.TryPauseAtProjectileSafePointAsync(
                    A<Func<bool>>._, A<Action>._))
                .ReturnsLazily((Func<bool> canPause, Action pause) =>
                {
                    if (!canPause()) return Task.FromResult(false);
                    pause();
                    return Task.FromResult(true);
                });
            A.CallTo(() => hostGame.SetPauseAsync(true)).Returns(Task.CompletedTask);
            A.CallTo(() => clientGame.SetPauseAsync(true)).Returns(Task.CompletedTask);
            A.CallTo(() => hostGame.SetTurnBasedCombatModeAsync(false)).Returns(Task.CompletedTask);
            A.CallTo(() => hostGame.TrySetTurnBasedCombatModeAsync(true, A<Func<bool>>._))
                .ReturnsLazily((bool _, Func<bool> canApply) => Task.FromResult(canApply()));
            A.CallTo(() => clientGame.SetTurnBasedCombatModeAsync(false)).Returns(Task.CompletedTask);
            A.CallTo(() => hostCombat.GetRealTimeCombatState()).Returns(new NetworkCombatState());
            A.CallTo(() => hostCombat.GetRealTimeCombatStateAsync())
                .ReturnsLazily(() => Task.FromResult(endState ?? new NetworkCombatState()));
            A.CallTo(() => hostCombat.ExecuteRealTimeCombatCommand(A<RealTimeCommandKind>._,
                A<NetworkUnitMoveTo>._, A<NetworkUnitAttack>._, A<NetworkAbilityUse>._)).Returns(true);
            A.CallTo(() => clientCombat.GetRealTimeCombatStateAsync()).Returns(Task.FromResult(new NetworkCombatState()));
            A.CallTo(() => clientCombat.TryApplyAndReadRealTimeCombatStateAsync(
                    A<NetworkCombatState>._, A<Func<bool>>._))
                .ReturnsLazily(() => Task.FromResult(endState ?? new NetworkCombatState()));
            A.CallTo(() => clientCombat.TryFinalizeRealTimeCombatAsync(A<Func<bool>>._, A<Action>._))
                .ReturnsLazily((Func<bool> canFinalize, Action finalize) =>
                {
                    if (!canFinalize()) return Task.FromResult(false);
                    finalize(); return Task.FromResult(true);
                });
            A.CallTo(() => clientCombat.TryExecuteRealTimeCombatCommandAsync(A<RealTimeCommandKind>._,
                A<NetworkUnitMoveTo>._, A<NetworkUnitAttack>._, A<NetworkAbilityUse>._, A<Func<bool>>._))
                .Returns(Task.FromResult<bool?>(true));
            var mainThread = A.Fake<IMainThreadAccessor>();
            A.CallTo(() => mainThread.Post(A<Action>._)).Invokes((Action action) =>
            {
                if (mainThreadQueue != null) mainThreadQueue.Enqueue(action);
                else action();
            });

            var hostNetwork = A.Fake<INetworkHostConnection>();
            var clientNetwork = A.Fake<INetworkClientConnection>();
            Fake.GetFakeManager(hostNetwork).AddRuleFirst(new NetworkReceiverFakeRule<INetworkConnection>());
            Fake.GetFakeManager(clientNetwork).AddRuleFirst(new NetworkReceiverFakeRule<INetworkConnection>());
            A.CallTo(() => hostNetwork.IsActive).Returns(true);
            A.CallTo(() => clientNetwork.IsActive).Returns(true);
            var queued = new Queue<object>();
            A.CallTo(() => hostNetwork.Broadcast(A<object>._)).Invokes(call => queued.Enqueue(call.Arguments[0]));
            A.CallTo(() => clientNetwork.Broadcast(A<object>._)).Invokes(call => queued.Enqueue(call.Arguments[0]));

            var leveling = A.Fake<ILevelingInteractionService>();
            var notification = A.Fake<IPlayerNotificationService>();
            var dialog = A.Fake<IDialogInteractionService>();
            var globalMap = A.Fake<IGlobalMapInteractionService>();
            var ping = A.Fake<IPingInteractionService>();
            var settings = A.Fake<IMultiplayerSettingsService>();
            var files = A.Fake<IFileSystemService>();
            var values = A.Fake<IValueGenerator>();
            var host = new MultiplayerHost(A.Fake<ILogger<MultiplayerHost>>(), hostGame, leveling, notification,
                dialog, globalMap, ping, hostCombat, settings, files, hostNetwork, values,
                A.Fake<IHashService>(), mapper, mainThread);
            var client = new MultiplayerClient(A.Fake<ILogger<MultiplayerClient>>(), clientGame, leveling,
                notification, dialog, globalMap, ping, clientCombat, settings, files, clientNetwork, values, mapper);
            host.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "integration-session",
                LocalPlayerId = NetworkConstants.HostPlayerId,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running }
            };
            host.Game.Players.Add(new NetworkPlayer(NetworkConstants.HostPlayerId) { LobbySyncStatus = NetworkLobbySyncStatus.Succeed });
            host.Game.Players.Add(new NetworkPlayer(42) { LobbySyncStatus = NetworkLobbySyncStatus.Succeed });
            client.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "integration-session",
                LocalPlayerId = 42,
                Combat = new NetworkCombat { Stage = NetworkCombatStage.Running }
            };
            return (host, client, hostNetwork, clientNetwork, queued, clientCombat);
        }

        private static void Pump(Queue<object> queued, INetworkHostConnection hostNetwork,
            INetworkClientConnection clientNetwork)
        {
            var delivered = 0;
            while (queued.Count > 0)
            {
                Assert.That(++delivered, Is.LessThan(20), "The protocol must converge without an unbounded message loop.");
                var message = queued.Dequeue();
                switch (message)
                {
                    case NotifyRealTimeCombatEnd end:
                        FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatEnd>(clientNetwork)(NetworkConstants.HostPlayerId, end);
                        break;
                    case ClientRealTimeCombatEndAck endAck:
                        FakeUtils.GetNetworkReceiverHandler<ClientRealTimeCombatEndAck>(hostNetwork)(42, endAck);
                        break;
                    case NotifyRealTimeModeTransition transition:
                        DeliverToClient(clientNetwork, transition);
                        break;
                    case ClientRealTimeModeTransitionAck ack:
                        FakeUtils.GetNetworkReceiverHandler<ClientRealTimeModeTransitionAck>(hostNetwork)(42, ack);
                        break;
                    case NotifyRealTimeRepair repair:
                        FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRepair>(clientNetwork)(NetworkConstants.HostPlayerId, repair);
                        break;
                    case ClientRealTimeRepairAck repairAck:
                        FakeUtils.GetNetworkReceiverHandler<ClientRealTimeRepairAck>(hostNetwork)(42, repairAck);
                        break;
                    case ClientRealTimeCombatCommand command:
                        FakeUtils.GetNetworkReceiverHandler<ClientRealTimeCombatCommand>(hostNetwork)(42, command);
                        break;
                    case NotifyRealTimeCombatCommandAccepted accepted:
                        FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatCommandAccepted>(clientNetwork)(NetworkConstants.HostPlayerId, accepted);
                        break;
                    case NotifyRealTimeCombatSnapshot snapshot:
                        FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeCombatSnapshot>(clientNetwork)(NetworkConstants.HostPlayerId, snapshot);
                        break;
                    case ClientRealTimeStateHash stateHash:
                        FakeUtils.GetNetworkReceiverHandler<ClientRealTimeStateHash>(hostNetwork)(42, stateHash);
                        break;
                    case NotifyRealTimeRuleRoll roll:
                        FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRuleRoll>(clientNetwork)
                            (NetworkConstants.HostPlayerId, roll);
                        break;
                    case ClientRealTimeRuleRollFault fault:
                        FakeUtils.GetNetworkReceiverHandler<ClientRealTimeRuleRollFault>(hostNetwork)(42, fault);
                        break;
                    case ClientRealTimeCommandFault commandFault:
                        FakeUtils.GetNetworkReceiverHandler<ClientRealTimeCommandFault>(hostNetwork)(42, commandFault);
                        break;
                    default:
                        Assert.Fail($"Unexpected protocol message: {message.GetType().Name}");
                        break;
                }
            }
        }

        private static void PumpUntilComplete(Queue<object> queued, INetworkHostConnection hostNetwork,
            INetworkClientConnection clientNetwork)
        {
            for (var count = 0; queued.Count > 0; count++)
            {
                Assert.That(count, Is.LessThan(20));
                if (queued.Peek() is NotifyRealTimeModeTransition nextTransition
                    && nextTransition.Stage == (int)RealTimeModeTransitionStage.Complete)
                {
                    return;
                }
                var message = queued.Dequeue();
                switch (message)
                {
                    case NotifyRealTimeModeTransition transition:
                        DeliverToClient(clientNetwork, transition);
                        break;
                    case ClientRealTimeModeTransitionAck ack:
                        FakeUtils.GetNetworkReceiverHandler<ClientRealTimeModeTransitionAck>(hostNetwork)(42, ack);
                        break;
                    default:
                        Assert.Fail($"Unexpected protocol message: {message.GetType().Name}");
                        break;
                }
            }
            Assert.Fail("The host did not send a completion checkpoint.");
        }

        private static void PumpUntilRepairResume(Queue<object> queued, INetworkHostConnection hostNetwork,
            INetworkClientConnection clientNetwork)
        {
            for (var count = 0; queued.Count > 0; count++)
            {
                Assert.That(count, Is.LessThan(20));
                if (queued.Peek() is NotifyRealTimeRepair nextRepair
                    && nextRepair.Stage == (int)RealTimeRepairStage.Resume)
                {
                    return;
                }
                var message = queued.Dequeue();
                switch (message)
                {
                    case NotifyRealTimeRepair repair:
                        FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeRepair>(clientNetwork)(NetworkConstants.HostPlayerId, repair);
                        break;
                    case ClientRealTimeRepairAck ack:
                        FakeUtils.GetNetworkReceiverHandler<ClientRealTimeRepairAck>(hostNetwork)(42, ack);
                        break;
                    default:
                        Assert.Fail($"Unexpected protocol message: {message.GetType().Name}");
                        break;
                }
            }
            Assert.Fail("The host did not send a repair Resume message.");
        }

        private static void DeliverToClient(INetworkClientConnection network, NotifyRealTimeModeTransition message) =>
            FakeUtils.GetNetworkReceiverHandler<NotifyRealTimeModeTransition>(network)(NetworkConstants.HostPlayerId, message);
    }
}
