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
        internal RealTimeNativeWorldLoadResult Receipt { get; private set; }
        internal bool IsLoaded => Receipt != null && _authorized();

        internal RealTimeNativeLoadOperation(Func<bool> authorized)
            => _authorized = authorized ?? throw new ArgumentNullException(nameof(authorized));

        internal async Task RunAsync(Func<Task<RealTimeNativeWorldLoadResult>> load)
        {
            if (load == null) throw new ArgumentNullException(nameof(load));
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
                throw new InvalidOperationException("native-load-checkpoint-already-started");
            if (!_authorized()) throw new InvalidOperationException("native-load-barrier-revoked");
            var receipt = await load();
            if (!_authorized()) throw new InvalidOperationException("native-load-barrier-revoked");
            if (receipt?.World == null || receipt.RepairObligation == null)
                throw new InvalidOperationException("native-load-receipt-incomplete");
            Receipt = receipt;
        }
    }
}
