using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WOTRMultiplayer.Services.CombatSaving
{
    internal sealed class CombatSaveRefusedException : Exception
    {
        internal CombatSaveRefusedException(string reason) : base(reason) { }
    }

    internal sealed class CombatSaveIdentity : IEquatable<CombatSaveIdentity>
    {
        public string Session { get; }
        public string Load { get; }
        public long Epoch { get; }
        public Guid Operation { get; }
        public CombatSaveIdentity(string session, string load, long epoch, Guid operation)
        {
            if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(load) || epoch < 0 || operation == Guid.Empty)
                throw new ArgumentException("combat-save-identity-invalid");
            Session = session; Load = load; Epoch = epoch; Operation = operation;
        }
        public bool Equals(CombatSaveIdentity other) => other != null && Session == other.Session
            && Load == other.Load && Epoch == other.Epoch && Operation == other.Operation;
        public override bool Equals(object other) => Equals(other as CombatSaveIdentity);
        public override int GetHashCode() => Operation.GetHashCode();
    }

    internal sealed class CombatSaveAcknowledgement
    {
        public CombatSaveIdentity Identity { get; set; }
        public long Player { get; set; }
        public string Digest { get; set; }
    }

    // The adapter must freeze commands before CaptureDigest, retain the native world,
    // and publish a fresh file atomically. No adapter is enabled before runtime validation.
    internal interface ICombatSaveRuntime
    {
        Task<T> OnMainThreadAsync<T>(Func<T> action);
        string GetRefusal();
        CombatSaveIdentity CreateIdentity();
        IReadOnlyCollection<long> ConnectedPeers { get; }
        IDisposable AcquireOwnedPause(CombatSaveIdentity identity);
        string CaptureDigest();
        bool IsCurrent(CombatSaveIdentity identity);
        Task<IReadOnlyCollection<CombatSaveAcknowledgement>> PreparePeersAsync(CombatSaveIdentity identity, CancellationToken cancellation);
        Task<string> WriteNewSaveAsync(CombatSaveIdentity identity, string digest, CancellationToken cancellation);
        void Finished(CombatSaveIdentity identity, string result, string path);
        void DivergenceDetected(CombatSaveIdentity identity);
    }

    internal sealed class CombatSaveCoordinator
    {
        private readonly ICombatSaveRuntime _runtime;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private int _busy;
        public CombatSaveCoordinator(ICombatSaveRuntime runtime,
            Func<TimeSpan, CancellationToken, Task> delay = null)
        { _runtime = runtime; _delay = delay ?? Task.Delay; }

        public async Task<string> SaveAsync(CancellationToken cancellation = default)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return "busy";
            CombatSaveIdentity identity = null;
            IDisposable pause = null;
            var result = "failed";
            string path = null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var refusal = await _runtime.OnMainThreadAsync(_runtime.GetRefusal);
                if (refusal != null) return result = refusal;
                var peers = await _runtime.OnMainThreadAsync(() =>
                {
                    identity = _runtime.CreateIdentity();
                    var expected = new HashSet<long>(_runtime.ConnectedPeers);
                    pause = _runtime.AcquireOwnedPause(identity);
                    return expected;
                });
                var preparation = _runtime.PreparePeersAsync(identity, deadline.Token);
                var timeout = _delay(TimeSpan.FromSeconds(15), deadline.Token);
                if (await Task.WhenAny(preparation, timeout) != preparation)
                {
                    // A late response has no authority to write anything; the adapter
                    // must scope every message to this identity and observe cancellation.
                    deadline.Cancel();
                    _ = preparation.ContinueWith(t => { _ = t.Exception; },
                        TaskContinuationOptions.OnlyOnFaulted);
                    return result = cancellation.IsCancellationRequested ? "cancelled" : "timeout";
                }
                var acknowledgements = await preparation;
                cancellation.ThrowIfCancellationRequested();
                if (!await _runtime.OnMainThreadAsync(() => _runtime.IsCurrent(identity) && peers.SetEquals(_runtime.ConnectedPeers)))
                    return result = "session-changed";
                refusal = await _runtime.OnMainThreadAsync(_runtime.GetRefusal);
                if (refusal != null) return result = refusal;
                if (acknowledgements == null || acknowledgements.Count != peers.Count
                    || acknowledgements.Any(a => a == null || !identity.Equals(a.Identity))
                    || !peers.SetEquals(acknowledgements.Select(a => a.Player)))
                    return result = "invalid-acknowledgements";
                // Capturing before Prepare lets the host observe the frame in which it
                // acquired the freeze while peers observe the following stable frame.
                // Wait until every peer is frozen and has captured its state, then take
                // both host checks inside the same owned barrier.
                var digest = await _runtime.OnMainThreadAsync(_runtime.CaptureDigest);
                if (string.IsNullOrWhiteSpace(digest)) return result = "invalid-digest";
                if (acknowledgements.Any(a => a.Digest != digest) || await _runtime.OnMainThreadAsync(_runtime.CaptureDigest) != digest)
                {
                    await _runtime.OnMainThreadAsync(() => { _runtime.DivergenceDetected(identity); return true; });
                    return result = "divergence";
                }
                // Do not release the pause on a write timeout: await the writer's terminal
                // result, so it cannot publish a save after we announce cancellation.
                path = await _runtime.WriteNewSaveAsync(identity, digest, cancellation);
                if (string.IsNullOrWhiteSpace(path)) return result = "write-failed";
                return result = "saved";
            }
            catch (CombatSaveRefusedException) { return result = "peer-refused"; }
            catch (OperationCanceledException) { return result = "cancelled"; }
            catch { result = "failed"; throw; }
            finally
            {
                try
                {
                    deadline.Cancel();
                    await _runtime.OnMainThreadAsync(() =>
                    {
                        try { if (identity != null) _runtime.Finished(identity, result, path); }
                        finally { pause?.Dispose(); }
                        return true;
                    });
                }
                finally { Interlocked.Exchange(ref _busy, 0); }
            }
        }
    }
}
