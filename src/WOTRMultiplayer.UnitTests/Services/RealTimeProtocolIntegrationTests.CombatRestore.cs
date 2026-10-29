using System;
using System.Collections.Generic;
using System.Reflection;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class RealTimeProtocolIntegrationTests
    {
        [Test]
        public void CombatRestore_LoadDeadlineOutlivesNativeLoaderDeadline()
        {
            var timeout = (TimeSpan)typeof(MultiplayerHost).GetField("CombatRestoreLoadTimeout",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

            Assert.That(timeout, Is.GreaterThan(TimeSpan.FromMinutes(2)),
                "The host barrier must still be alive when a valid peer finishes the native two-minute load window.");
        }

        [Test]
        public void CombatRestore_ExpiredOperationFromPreviousLoadCannotPauseReplacement()
        {
            var (host, client, _, _, _, _) = CreatePair();
            host.Game.Stage = client.Game.Stage = NetworkLobbyStage.Playing;
            var oldHost = new NotifyCombatSave { SessionId = host.Game.Id, SessionSeed = host.Game.SessionSeed,
                LoadedSaveSeed = host.Game.LoadedSaveSeed, Epoch = host.Game.RealTimeCombat.Epoch, Operation = Guid.NewGuid().ToString("N") };
            var oldClient = new NotifyCombatSave { SessionId = client.Game.Id, SessionSeed = client.Game.SessionSeed,
                LoadedSaveSeed = client.Game.LoadedSaveSeed, Epoch = client.Game.RealTimeCombat.Epoch, Operation = oldHost.Operation };
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MultiplayerHost).GetField("_combatRestore", flags).SetValue(host, oldHost);
            typeof(MultiplayerClient).GetField("_combatRestoreHeader", flags).SetValue(client, oldClient);
            host.Game.LoadedSaveSeed++;
            client.Game.LoadedSaveSeed++;
            try
            {
                host.TickCombatSaveRestore();
                client.TickCombatSave();
                Assert.That(typeof(MultiplayerHost).GetField("_combatRestore", flags).GetValue(host), Is.Null);
                Assert.That(typeof(MultiplayerClient).GetField("_combatRestoreHeader", flags).GetValue(client), Is.Null);
                var gameField = typeof(MultiplayerActorBase).GetProperty("GameInteraction", flags);
                var hostGame = (WOTRMultiplayer.Abstractions.GameInteraction.IGameInteractionService)gameField.GetValue(host);
                var clientGame = (WOTRMultiplayer.Abstractions.GameInteraction.IGameInteractionService)gameField.GetValue(client);
                A.CallTo(() => hostGame.SetPause(true)).MustNotHaveHappened();
                A.CallTo(() => clientGame.SetPause(true)).MustNotHaveHappened();
            }
            finally { host.Reset(); client.Reset(); }
        }

        [Test]
        public void CombatRestore_DoesNotAdvanceBeforeOnGameLoaded()
        {
            var (host, _, hostNetwork, _, queued, _) = CreatePair();
            host.Game.Stage = NetworkLobbyStage.Playing;
            host.CombatSaveDigestReader = () => new string('A', 64);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MultiplayerActorBase).GetField("PendingCombatSave", flags).SetValue(host,
                new CombatSaveManifest { CanonicalDigest = new string('A', 64), RestoreDigest = new string('A', 64) });
            var header = new NotifyCombatSave { SessionId = host.Game.Id, SessionSeed = host.Game.SessionSeed,
                LoadedSaveSeed = host.Game.LoadedSaveSeed, Epoch = host.Game.RealTimeCombat.Epoch,
                Operation = Guid.NewGuid().ToString("N"), Stage = "RestorePrepare" };
            typeof(MultiplayerHost).GetField("_combatRestore", flags).SetValue(host, header);
            typeof(MultiplayerHost).GetField("_combatRestoreDeadline", flags)
                .SetValue(host, DateTime.UtcNow.AddMinutes(1));
            var peers = (HashSet<long>)typeof(MultiplayerHost).GetField("_combatRestorePeers", flags).GetValue(host);
            peers.UnionWith(host.Game.Players.FindAll(player => player.Id != host.Game.LocalPlayerId)
                .ConvertAll(player => player.Id));
            A.CallTo(() => hostNetwork.Send(42, A<object>._))
                .Invokes(call => queued.Enqueue(call.Arguments[1]));
            try
            {
                host.TickCombatSaveRestore();
                Assert.That(header.Stage, Is.EqualTo("RestorePrepare"));
                Assert.That(queued, Is.Empty);

                typeof(MultiplayerActorBase).GetField("_combatSaveGameLoaded", flags).SetValue(host, true);
                host.TickCombatSaveRestore();
                Assert.That(((NotifyCombatSave)queued.Dequeue()).Stage, Is.EqualTo("RestorePrepare"));
            }
            finally { CombatSaveLoadState.Owner = null; host.Reset(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CombatRestore_LostFinalReceiptIsRetriedBeforeHostReleasesBarrier(bool diverge)
        {
            var (host, client, hostNetwork, _, queued, _) = CreatePair();
            host.Game.Stage = client.Game.Stage = NetworkLobbyStage.Playing;
            client.Game.SessionSeed = host.Game.SessionSeed;
            client.Game.LoadedSaveSeed = host.Game.LoadedSaveSeed;
            var digest = new string('A', 64);
            host.CombatSaveDigestReader = () => digest;
            client.CombatSaveDigestReader = () => diverge ? new string('B', 64) : digest;
            var pending = typeof(MultiplayerActorBase).GetField("PendingCombatSave", BindingFlags.Instance | BindingFlags.NonPublic);
            pending.SetValue(host, new CombatSaveManifest { CanonicalDigest = digest, RestoreDigest = digest });
            pending.SetValue(client, new CombatSaveManifest { CanonicalDigest = digest, RestoreDigest = digest });
            typeof(MultiplayerActorBase).GetField("_combatSaveGameLoaded", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(host, true);
            typeof(MultiplayerActorBase).GetField("_combatSaveGameLoaded", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(client, true);
            var header = new NotifyCombatSave { SessionId = host.Game.Id, SessionSeed = host.Game.SessionSeed,
                LoadedSaveSeed = host.Game.LoadedSaveSeed, Epoch = host.Game.RealTimeCombat.Epoch,
                Operation = Guid.NewGuid().ToString("N"), Stage = "RestorePrepare" };
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            void Set(string name, object value) => typeof(MultiplayerHost).GetField(name,
                flags).SetValue(host, value);
            Set("_combatRestore", header);
            Set("_combatRestoreDeadline", DateTime.UtcNow.AddMinutes(1));
            ((HashSet<long>)typeof(MultiplayerHost).GetField("_combatRestorePeers", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(host)).Add(42);
            // A verified combat-save load is the documented recovery path after
            // an earlier save/turn barrier failed.  That old latch must not
            // reject RestorePrepare for the newly loaded host archive.
            typeof(MultiplayerHost).GetField("_turnEndSynchronizationFailed", flags).SetValue(host, true);
            typeof(MultiplayerClient).GetField("_turnEndSynchronizationFailed", flags).SetValue(client, true);
            A.CallTo(() => hostNetwork.Send(42, A<object>._)).Invokes(call => queued.Enqueue(ProtoBuf.Serializer.DeepClone((NotifyCombatSave)call.Arguments[1])));
            // Start after native world restoration. Sender/identity gates are tested
            // separately; this exercise drives the real stage handlers and retries.
            void Client(NotifyCombatSave frame) => typeof(MultiplayerClient).GetMethod("HandleCombatSaveRestore",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(client, new object[] { frame });
            void Host(ClientCombatSaveAck ack) => typeof(MultiplayerHost).GetMethod("AcceptCombatRestoreAck",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(host, new object[] { 42L, ack });
            try
            {
                var premature = ProtoBuf.Serializer.DeepClone(header); premature.Stage = "RestoreRelease";
                Client(premature);
                Assert.That(pending.GetValue(client), Is.Not.Null);
                Assert.That(queued, Is.Empty);
                host.TickCombatSaveRestore();
                Client((NotifyCombatSave)queued.Dequeue());
                Host((ClientCombatSaveAck)queued.Dequeue());
                if (diverge)
                {
                    Assert.That(((NotifyCombatSave)queued.Dequeue()).Stage, Is.EqualTo("RestoreFailed"));
                    Assert.That(host.IsSavingBlockedByCombatSync, Is.True);
                    Assert.That(pending.GetValue(host), Is.Not.Null);
                    return;
                }
                Client((NotifyCombatSave)queued.Dequeue()); // Commit
                Host((ClientCombatSaveAck)queued.Dequeue());
                var complete = (NotifyCombatSave)queued.Dequeue();
                Assert.That(complete.Stage, Is.EqualTo("RestoreComplete"));
                Client(complete);
                queued.Dequeue(); // Lose client's final receipt.
                Assert.That(pending.GetValue(host), Is.Not.Null, "Host must retain the load barrier.");
                Assert.That(pending.GetValue(client), Is.Not.Null, "Client must not tick while its receipt is unconfirmed.");
                Set("_combatRestoreNext", DateTime.MinValue);
                host.TickCombatSaveRestore();
                Client((NotifyCombatSave)queued.Dequeue());
                Host((ClientCombatSaveAck)queued.Dequeue());
                Assert.That(pending.GetValue(host), Is.Null);
                Assert.That(pending.GetValue(client), Is.Not.Null);
                var release = (NotifyCombatSave)queued.Dequeue();
                Assert.That(release.Stage, Is.EqualTo("RestoreRelease"));
                Client(release);
                Assert.That(pending.GetValue(client), Is.Null);
                Assert.That(typeof(MultiplayerHost).GetField("_turnEndSynchronizationFailed", flags).GetValue(host), Is.False);
                Assert.That(typeof(MultiplayerClient).GetField("_turnEndSynchronizationFailed", flags).GetValue(client), Is.False);
                Client(release); // Idempotent release must neither restart nor acknowledge a turn.
                Assert.That(queued, Is.Empty);
            }
            finally { CombatSaveLoadState.Owner = null; host.Reset(); client.Reset(); }
        }
    }
}
