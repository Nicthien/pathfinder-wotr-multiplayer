using System;
using System.Threading.Tasks;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerClient
    {
        // Reconstruction coordinator owns the enclosing barrier. A returned
        // receipt is not a native graph verification, ACK/Commit or resume.
        internal async Task<RealTimeNativeWorldLoadResult> LoadTransferredNativeCheckpointAsync(
            NotifyRealTimeNativeCheckpoint expected, Func<bool> barrierOwned)
        {
            if (barrierOwned == null) throw new ArgumentNullException(nameof(barrierOwned));
            if (!RealTimeNativeCheckpointLimits.IsManifest(expected))
                throw new InvalidOperationException("native-load-manifest-invalid");
            var manifest = RealTimeNativeCheckpointLimits.Copy(expected);
            using var source = CloneTransferredNativeCheckpoint(manifest);
            bool Authorized()
            {
                lock (ActionLock)
                    return barrierOwned() && IsCurrentNativeReceive() && !_nativeReceiveFailed
                        && _nativeReceivedSource != null && _nativeReceiver?.MatchesManifest(manifest) == true;
            }
            return await RealTimeNativeClientLoad.LoadAsync(_mainThreadAccessor, this, CombatInteraction, source,
                manifest.SessionId, manifest.CheckpointId, Authorized);
        }
    }
}
