using System;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerHost
    {
        private bool CanPublishRealTimeSnapshot()
        {
            var epoch = Game.RealTimeCombat.Epoch;
            foreach (var key in _realTimeSnapshotPendingPeers.Keys)
                if (key.Epoch != epoch) _realTimeSnapshotPendingPeers.TryRemove(key, out _);
            foreach (var entry in _realTimeSnapshotSentAt)
            {
                if (entry.Key.Epoch != epoch)
                {
                    _realTimeSnapshotSentAt.TryRemove(entry.Key, out _);
                    continue;
                }
                // At most one snapshot awaits application by all peers. This
                // keeps costly native reconciliation out of a growing FIFO and
                // leaves room for pause, command and repair messages.
                if (DateTime.UtcNow - entry.Value < TimeSpan.FromSeconds(8)) return false;
                _realTimeSnapshotSentAt.TryRemove(entry.Key, out _);
                _realTimeSnapshotHashes.TryRemove(entry.Key, out _);
                foreach (var key in _realTimeSnapshotPendingPeers.Keys)
                    if (key.Epoch == entry.Key.Epoch && key.Sequence == entry.Key.Sequence)
                        _realTimeSnapshotPendingPeers.TryRemove(key, out _);
                Game.RealTimeCombat.MissingSnapshotReplies++;
                // The former 33-frame window at 250 ms expired near eight
                // seconds. Keep that bound with a wall-clock deadline, even
                // though publication now waits for application acknowledgments.
                Logger.LogWarning("Real-time snapshot application reply expired; pausing for verification. Epoch={Epoch}, Sequence={Sequence}", epoch, entry.Key.Sequence);
                _ = BeginRealTimeRepairAsync();
                return false;
            }
            return true;
        }
    }
}
