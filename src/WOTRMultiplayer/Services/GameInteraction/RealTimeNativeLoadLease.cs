using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Kingmaker.EntitySystem.Persistence;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // A single local native load owns the lifecycle suppression and stash
    // capability. It never authorizes a rule, a Commit, resume or saving.
    internal sealed class RealTimeNativeLoadLease : IDisposable
    {
        private static readonly object Gate = new();
        private static readonly AsyncLocal<RealTimeNativeLoadLease> Hydration = new();
        private static RealTimeNativeLoadLease _current;
        private readonly Func<bool> _authorized;
        private readonly Action<string, byte[]> _write;
        private readonly HashSet<string> _files;
        private int _disposed;
        private int _admitted;
        private int _completed;
        private int _workersDrained;
        private string _fault;
        internal object Owner { get; }
        internal SaveInfo Info { get; }
        internal bool NativeCallbackCompleted => Volatile.Read(ref _completed) != 0;
        internal string Fault => Volatile.Read(ref _fault);
        internal bool IsDrainedAndCompleted => Volatile.Read(ref _disposed) != 0
            && Volatile.Read(ref _workersDrained) != 0 && NativeCallbackCompleted && Fault == null;
        internal static bool IsActive => Volatile.Read(ref _current) != null;
        internal static bool IsHydrating => Hydration.Value is { } lease
            && ReferenceEquals(Volatile.Read(ref _current), lease) && lease.IsAuthorized;
        internal bool IsAuthorized => Volatile.Read(ref _disposed) == 0 && Fault == null && _authorized();

        private RealTimeNativeLoadLease(object owner, SaveInfo info, Func<bool> authorized,
            Action<string, byte[]> write, IEnumerable<string> files)
        { Owner = owner; Info = info; _authorized = authorized; _write = write; _files = new(files, StringComparer.Ordinal); }

        internal static RealTimeNativeLoadLease Begin(object owner, SaveInfo info, Func<bool> authorized,
            Action<string, byte[]> write)
        {
            if (owner == null || authorized == null || write == null || !authorized()
                || info?.Saver is not RealTimeMemorySaver source || !source.IsSealed
                || string.IsNullOrEmpty(info.FolderName)
                || !info.FolderName.StartsWith("RTwP-volatile-", StringComparison.Ordinal)
                || !info.FolderName.EndsWith(".memory", StringComparison.Ordinal))
                throw new InvalidOperationException("native-load-lease-not-authorized");
            var files = source.GetAllFiles();
            if (files.Exists(name => name.Equals("lock", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("native-load-stash-lock-forbidden");
            var lease = new RealTimeNativeLoadLease(owner, info, authorized, write, files);
            lock (Gate)
            {
                if (_current != null) throw new InvalidOperationException("native-load-already-owned");
                info.Saver = source.WithNativeStashWriter(lease, lease.WriteStash);
                source.Dispose();
                Volatile.Write(ref _current, lease);
            }
            return lease;
        }

        internal static bool SuppressesLifecycle(object owner) => owner != null
            && ReferenceEquals(Volatile.Read(ref _current)?.Owner, owner);

        // Only the exact first LoadArea call is admitted. A user load, another
        // area or a repeated native entry must not race this reconstruction.
        internal static bool AdmitAreaLoad(SaveInfo info)
        {
            var lease = Volatile.Read(ref _current);
            if (lease == null) return true;
            return ReferenceEquals(info, lease.Info) && lease.IsAuthorized
                && Interlocked.CompareExchange(ref lease._admitted, 1, 0) == 0;
        }

        private void WriteStash(string name, byte[] bytes)
        {
            RealTimeMemorySaver.ValidateFileName(name);
            if (!IsAuthorized || Volatile.Read(ref _admitted) == 0 || Fault != null || !_files.Contains(name))
                throw new InvalidOperationException("native-load-stash-write-revoked");
            _write(name, bytes);
        }

        internal static bool RecordFault(string reason)
        {
            var lease = Volatile.Read(ref _current);
            if (lease == null) return false;
            lease.Fail(reason);
            return true;
        }
        internal void Fail(string reason) => Interlocked.CompareExchange(ref _fault,
            string.IsNullOrWhiteSpace(reason) ? "native-load-unspecified-failure" : reason.Substring(0, Math.Min(reason.Length, 320)), null);

        internal void MarkNativeCallback()
        {
            if (!IsAuthorized || Fault != null || Volatile.Read(ref _admitted) == 0) return;
            Interlocked.Exchange(ref _completed, 1);
        }

        // Called only after a positive observation that the native loading
        // process has drained. Disposal or an observation timeout is not proof.
        internal void MarkWorkersDrained() => Interlocked.Exchange(ref _workersDrained, 1);

        internal static IDisposable EnterHydration(SaveInfo info)
        {
            var lease = Volatile.Read(ref _current);
            return lease != null && lease.IsAuthorized && Volatile.Read(ref lease._admitted) != 0
                && info?.FolderName == lease.Info.FolderName
                && info.Saver is RealTimeMemorySaver source && source.IsNativeLoadSource(lease)
                ? new HydrationScope(lease) : null;
        }
        internal static IDisposable EnterAreaHydration(string areaId)
        {
            var lease = Volatile.Read(ref _current);
            return lease != null && lease.IsAuthorized && Volatile.Read(ref lease._admitted) != 0
                && Guid.TryParseExact(areaId, "N", out _) && lease._files.Contains(areaId + ".json")
                ? new HydrationScope(lease) : null;
        }
        private sealed class HydrationScope : IDisposable
        {
            private readonly RealTimeNativeLoadLease _previous;
            private bool _disposed;
            internal HydrationScope(RealTimeNativeLoadLease lease)
            { _previous = Hydration.Value; Hydration.Value = lease; }
            public void Dispose()
            { if (_disposed) return; _disposed = true; Hydration.Value = _previous; }
        }
        public void Dispose()
        {
            lock (Gate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                if (ReferenceEquals(_current, this)) Volatile.Write(ref _current, null);
            }
            Info.Saver?.Dispose();
        }
    }
}
