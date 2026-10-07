using System;
using System.Threading.Tasks;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services.GameInteraction;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerClient
    {
        private object _nativeReferenceRestorationOwner;

        // Data/hash verification only. A successful result is deliberately not
        // a mechanical graph certificate, a sequence Commit or a resume vote.
        internal async Task<ulong?> RestoreLoadedNativeReferenceAsync(NotifyRealTimeNativeCheckpoint expected,
            NetworkCombatState reference, Func<bool> barrierOwned)
        {
            if (barrierOwned == null) throw new ArgumentNullException(nameof(barrierOwned));
            if (!RealTimeNativeCheckpointLimits.IsManifest(expected) || reference == null)
                throw new InvalidOperationException("native-reference-manifest-or-state-invalid");
            var manifest = RealTimeNativeCheckpointLimits.Copy(expected);
            var checkpoint = Mapper.Map<NetworkCombatState>(ProtoBuf.Serializer.DeepClone(
                Mapper.Map<Networking.Messages.Contracts.NetworkCombatState>(reference)));
            RealTimeNativeTopologyComparison.RequireValid(checkpoint.NativeTopology);
            RealTimeAcceptedOrderReferenceValidation.RequireIdentity(checkpoint.AcceptedOrderReference,
                manifest.SessionId, manifest.LoadedSaveSeed, manifest.Epoch, manifest.LastIncludedSequence);
            if (RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = checkpoint }) != manifest.CanonicalStateHash)
                throw new InvalidOperationException("native-reference-not-bound-to-manifest");
            var owner = new object();
            RealTimeNativeLoadOperation operation;
            lock (ActionLock)
            {
                operation = _nativeLoadOperation;
                if (_nativeReferenceRestorationOwner != null || !barrierOwned() || !IsCurrentNativeReceive()
                    || _nativeReceiveFailed || _nativeReceiver?.MatchesManifest(manifest) != true || operation?.IsLoaded != true)
                    throw new InvalidOperationException("native-reference-loaded-receipt-unavailable");
                _nativeReferenceRestorationOwner = owner;
            }
            bool Authorized()
            {
                lock (ActionLock)
                    return ReferenceEquals(_nativeReferenceRestorationOwner, owner) && barrierOwned()
                        && ReferenceEquals(_nativeLoadOperation, operation) && operation.IsLoaded
                        && IsCurrentNativeReceive() && !_nativeReceiveFailed
                        && _nativeReceiver?.MatchesManifest(manifest) == true;
            }
            try
            {
                var receipt = operation.Receipt;
                var actual = await CombatInteraction.TryRestoreNativeReferenceStateAsync(receipt.World,
                    receipt.RepairObligation, checkpoint, Authorized);
                if (!Authorized() || actual == null) return null;
                var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = actual });
                var difference = RealTimeCombatTemporalDiff.Summarize(checkpoint, actual);
                if (hash != manifest.CanonicalStateHash || difference != null)
                    throw new InvalidOperationException("native-reference-restoration-differs:" + difference);
                var topologyDifference = RealTimeNativeTopologyComparison.Difference(checkpoint.NativeTopology, actual.NativeTopology);
                if (topologyDifference != null) throw new InvalidOperationException("native-reference-topology-differs:" + topologyDifference);
                lock (ActionLock)
                {
                    RealTimeAcceptedOrderLedger.RestoreReference(Game?.RealTimeCombat, checkpoint.AcceptedOrderReference,
                        manifest.SessionId, manifest.LoadedSaveSeed, manifest.Epoch, manifest.LastIncludedSequence, Authorized);
                }
                return hash;
            }
            finally
            {
                lock (ActionLock)
                    if (ReferenceEquals(_nativeReferenceRestorationOwner, owner)) _nativeReferenceRestorationOwner = null;
            }
        }

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
