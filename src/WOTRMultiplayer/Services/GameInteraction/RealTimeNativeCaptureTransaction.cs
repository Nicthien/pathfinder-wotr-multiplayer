using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Preparation may invoke native callbacks which fail after changing state.
    // Register each compensation BEFORE invoking the corresponding callback.
    public sealed class RealTimeNativeCaptureTransaction : IDisposable
    {
        private readonly Stack<Action> _restore = new();
        private bool _disposed;

        public void Apply(Action prepare, Action restore)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RealTimeNativeCaptureTransaction));
            if (prepare == null) throw new ArgumentNullException(nameof(prepare));
            if (restore == null) throw new ArgumentNullException(nameof(restore));
            _restore.Push(restore);
            prepare();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var failures = new List<Exception>();
            while (_restore.Count != 0)
            {
                try { _restore.Pop()(); }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count != 0)
                throw new AggregateException("native-checkpoint-host-restoration-failed", failures);
        }

        public static RealTimeMemorySaver Capture(Action<RealTimeNativeCaptureTransaction> prepare,
            Action<RealTimeMemorySaver> serialize, Func<bool> verifyRestored)
        {
            if (prepare == null) throw new ArgumentNullException(nameof(prepare));
            if (serialize == null) throw new ArgumentNullException(nameof(serialize));
            if (verifyRestored == null) throw new ArgumentNullException(nameof(verifyRestored));
            var source = new RealTimeMemorySaver();
            var transaction = new RealTimeNativeCaptureTransaction();
            Exception failure = null;
            try { prepare(transaction); serialize(source); }
            catch (Exception error) { failure = error; }
            try { transaction.Dispose(); }
            catch (Exception error)
            {
                failure = failure == null ? error
                    : new AggregateException("native-checkpoint-capture-and-restoration-failed", failure, error);
            }
            try
            {
                // No source is published before all restoration callbacks have
                // run and the host has passed its unchanged-world verification.
                if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
                if (!verifyRestored()) throw new InvalidOperationException("native-checkpoint-host-changed");
                source.Seal();
                return source;
            }
            catch { source.Dispose(); throw; }
        }
    }
}
