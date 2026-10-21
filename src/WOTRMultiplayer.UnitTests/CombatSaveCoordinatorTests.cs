using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.UnitTests
{
    public class CombatSaveCoordinatorTests
    {
        private sealed class Runtime : ICombatSaveRuntime
        {
            public Task<T> OnMainThreadAsync<T>(Func<T> action) => Task.FromResult(action());
            public string Refusal;
            public bool Current = true, Divergent, Paused;
            public int Writes, Releases, Captures;
            public string Outcome, Digest = "host-state";
            public IReadOnlyCollection<long> ConnectedPeers { get; set; } = new long[] { 2 };
            public CombatSaveIdentity Identity = new("session", "load", 1, Guid.NewGuid());
            public Func<Runtime, Task<IReadOnlyCollection<CombatSaveAcknowledgement>>> Prepare;
            public bool FailWrite;
            public string GetRefusal() => Refusal;
            public CombatSaveIdentity CreateIdentity() => Identity;
            public IDisposable AcquireOwnedPause(CombatSaveIdentity identity)
            { Paused = true; return new Lease(() => { Paused = false; Releases++; }); }
            public string CaptureDigest()
            {
                Captures++;
                return Digest;
            }
            public bool IsCurrent(CombatSaveIdentity identity) => Current;
            public Task<IReadOnlyCollection<CombatSaveAcknowledgement>> PreparePeersAsync(CombatSaveIdentity identity, CancellationToken cancellation)
                => Prepare?.Invoke(this) ?? Task.FromResult<IReadOnlyCollection<CombatSaveAcknowledgement>>(
                    new[] { Ack() });
            public CombatSaveAcknowledgement Ack() => new() { Identity = Identity, Player = 2, Digest = Digest };
            public Task<string> WriteNewSaveAsync(CombatSaveIdentity identity, string digest, CancellationToken cancellation)
            {
                Assert.That(Paused, Is.True);
                Writes++;
                if (FailWrite) throw new InvalidOperationException("disk-full");
                return Task.FromResult("new-save.zks");
            }
            public void Finished(CombatSaveIdentity identity, string result, string path) => Outcome = result;
            public void DivergenceDetected(CombatSaveIdentity identity) => Divergent = true;
        }
        private sealed class Lease : IDisposable
        {
            private readonly Action _release;
            public Lease(Action release) => _release = release;
            public void Dispose() => _release();
        }
        [Test]
        public async Task ConfirmedStateWritesOnceAndReleasesOnlyOwnedLease()
        {
            var runtime = new Runtime();
            Assert.That(await new CombatSaveCoordinator(runtime).SaveAsync(), Is.EqualTo("saved"));
            Assert.That(runtime.Writes, Is.EqualTo(1));
            Assert.That(runtime.Releases, Is.EqualTo(1));
        }
        [Test]
        public async Task HostDigestIsCapturedOnlyAfterPeersHaveFrozenAndPrepared()
        {
            var runtime = new Runtime { Digest = "host-before-peer-freeze" };
            runtime.Prepare = r =>
            {
                Assert.That(r.Captures, Is.Zero);
                r.Digest = "stable-frozen-state";
                return Task.FromResult<IReadOnlyCollection<CombatSaveAcknowledgement>>(new[] { r.Ack() });
            };

            Assert.That(await new CombatSaveCoordinator(runtime).SaveAsync(), Is.EqualTo("saved"));
            Assert.That(runtime.Captures, Is.EqualTo(2));
            Assert.That(runtime.Writes, Is.EqualTo(1));
        }
        [TestCase("stale")]
        [TestCase("missing")]
        [TestCase("duplicate")]
        [TestCase("wrong-peer")]
        public async Task InvalidAcknowledgementsNeverAuthorizeWrite(string scenario)
        {
            var runtime = new Runtime { Prepare = r =>
            {
                var ack = r.Ack();
                if (scenario == "stale") ack.Identity = new("session", "old-load", 1, r.Identity.Operation);
                if (scenario == "wrong-peer") ack.Player = 3;
                return Task.FromResult<IReadOnlyCollection<CombatSaveAcknowledgement>>(scenario == "missing"
                    ? Array.Empty<CombatSaveAcknowledgement>() : scenario == "duplicate" ? new[] { ack, ack } : new[] { ack });
            }};
            Assert.That(await new CombatSaveCoordinator(runtime).SaveAsync(), Is.EqualTo("invalid-acknowledgements"));
            Assert.That(runtime.Writes, Is.Zero);
            Assert.That(runtime.Releases, Is.EqualTo(1));
        }
        [Test]
        public async Task DivergenceTriggersRecoveryWithoutSaving()
        {
            var runtime = new Runtime { Prepare = r =>
            { var ack = r.Ack(); ack.Digest = "other"; return Task.FromResult<IReadOnlyCollection<CombatSaveAcknowledgement>>(new[] { ack }); }};
            Assert.That(await new CombatSaveCoordinator(runtime).SaveAsync(), Is.EqualTo("divergence"));
            Assert.That(runtime.Divergent, Is.True);
            Assert.That(runtime.Writes, Is.Zero);
        }
        [Test]
        public async Task DisconnectInvalidatesPreparation()
        {
            var runtime = new Runtime { Prepare = r =>
            { r.ConnectedPeers = Array.Empty<long>(); return Task.FromResult<IReadOnlyCollection<CombatSaveAcknowledgement>>(new[] { r.Ack() }); }};
            Assert.That(await new CombatSaveCoordinator(runtime).SaveAsync(), Is.EqualTo("session-changed"));
            Assert.That(runtime.Writes, Is.Zero);
        }
        [Test]
        public async Task DuplicateRequestRejectedAndLateAckCannotWriteAfterTimeout()
        {
            var response = new TaskCompletionSource<IReadOnlyCollection<CombatSaveAcknowledgement>>();
            var timeout = new TaskCompletionSource<bool>();
            var runtime = new Runtime { Prepare = _ => response.Task };
            var coordinator = new CombatSaveCoordinator(runtime, (duration, token) =>
            { Assert.That(duration, Is.EqualTo(TimeSpan.FromSeconds(15))); return timeout.Task; });
            var first = coordinator.SaveAsync();
            Assert.That(await coordinator.SaveAsync(), Is.EqualTo("busy"));
            timeout.SetResult(true);
            Assert.That(await first, Is.EqualTo("timeout"));
            response.SetResult(new[] { runtime.Ack() });
            Assert.That(runtime.Writes, Is.Zero);
            Assert.That(runtime.Releases, Is.EqualTo(1));
        }
        [Test]
        public async Task PeerRefusalIsAnAbortNotAWriteFailure()
        {
            var runtime = new Runtime { Prepare = _ => throw new CombatSaveRefusedException("action-in-progress") };
            Assert.That(await new CombatSaveCoordinator(runtime).SaveAsync(), Is.EqualTo("peer-refused"));
            Assert.That(runtime.Outcome, Is.EqualTo("peer-refused"));
            Assert.That(runtime.Writes, Is.Zero);
            Assert.That(runtime.Releases, Is.EqualTo(1));
            Assert.That(runtime.Divergent, Is.False);
        }

        [Test]
        public void FailedWriteReleasesPauseAndDoesNotReportSuccess()
        {
            var runtime = new Runtime { FailWrite = true };
            Assert.ThrowsAsync<InvalidOperationException>(() => new CombatSaveCoordinator(runtime).SaveAsync());
            Assert.That(runtime.Outcome, Is.EqualTo("failed"));
            Assert.That(runtime.Releases, Is.EqualTo(1));
        }
        [Test]
        public async Task RefusalDoesNotPauseOrWrite()
        {
            var runtime = new Runtime { Refusal = "unvalidated" };
            Assert.That(await new CombatSaveCoordinator(runtime).SaveAsync(), Is.EqualTo("unvalidated"));
            Assert.That(runtime.Releases, Is.Zero);
            Assert.That(runtime.Writes, Is.Zero);
        }
        [Test]
        public void EligibilityRejectsActiveActionAndAcceptsStablePlayerTurn()
        {
            var value = new CombatSaveEligibility { Host = true, TurnBased = true, Running = true, PlayerTurn = true, Initialized = true };
            Assert.That(value.Refusal(), Is.Null);
            value.ActionPending = true;
            Assert.That(value.Refusal(), Is.EqualTo("action-in-progress"));
            value.Divergent = true;
            Assert.That(value.Refusal(), Is.EqualTo("synchronization-required"));
        }
    }
}
