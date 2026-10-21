using System;
using System.Threading;
using System.Threading.Tasks;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Lifetime belongs to the checkpoint identity, not to a repeated packet.
    // Revoking the barrier cannot cancel native workers or authorize a retry.
    internal sealed class RealTimeNativeLoadOperation
    {
        private readonly Func<bool> _authorized;
        private int _started;
        private int _settled;
        private RealTimeNativeLoadLease _lease;
        internal RealTimeNativeWorldLoadResult Receipt { get; private set; }
        internal bool IsLoaded => Receipt != null && _authorized();
        // A faulted task alone is NOT worker completion: the loader can report
        // a failure while its drain coroutine still owns native work.
        internal bool CanAcknowledgeAbort => Volatile.Read(ref _settled) != 0
            && (Volatile.Read(ref _lease) is not { } lease || lease.IsDrained);

        internal void AttachLease(RealTimeNativeLoadLease lease)
        {
            if (lease == null) throw new ArgumentNullException(nameof(lease));
            if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _settled) != 0
                || Interlocked.CompareExchange(ref _lease, lease, null) != null)
                throw new InvalidOperationException("native-load-lease-already-bound-or-operation-inactive");
        }

        internal RealTimeNativeLoadOperation(Func<bool> authorized)
            => _authorized = authorized ?? throw new ArgumentNullException(nameof(authorized));

        internal async Task RunAsync(Func<Task<RealTimeNativeWorldLoadResult>> load)
        {
            if (load == null) throw new ArgumentNullException(nameof(load));
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
                throw new InvalidOperationException("native-load-checkpoint-already-started");
            try
            {
                if (!_authorized()) throw new InvalidOperationException("native-load-barrier-revoked");
                var receipt = await load();
                if (!_authorized()) throw new InvalidOperationException("native-load-barrier-revoked");
                if (receipt?.World == null || receipt.RepairObligation == null)
                    throw new InvalidOperationException("native-load-receipt-incomplete");
                Receipt = receipt;
            }
            finally { Volatile.Write(ref _settled, 1); }
        }
    }
}
