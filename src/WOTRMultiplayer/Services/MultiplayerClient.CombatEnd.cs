using System;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerClient
    {
        private NetworkGame _endReceiptSession;
        private int _endReceiptSave;
        private NotifyRealTimeCombatEnd _endReceipt;
        private int _endAppliedStage;
        private NetworkCombat _deferredEndCombat;
        private bool WaitingForHostCombatEnd => _deferredEndCombat != null && ReferenceEquals(Game?.Combat, _deferredEndCombat);

        protected override bool TryRetainRealTimeCombatOnStart() => RealTimePresentationRuntime.Enabled
            && Game?.Combat?.Stage == NetworkCombatStage.Running
            && (Game.RealTimeCombat?.Mode == CombatMode.RealTimeWithPause || WaitingForHostCombatEnd);

        protected override bool TryDeferRealTimeCombatEnd()
        {
            if (!RealTimePresentationRuntime.Enabled || Game?.Combat?.Stage != NetworkCombatStage.Running
                || Game.RealTimeCombat == null || !(Game.RealTimeCombat.Mode == CombatMode.RealTimeWithPause
                    || Game.RealTimeCombat.PreviousMode == CombatMode.RealTimeWithPause
                    || WaitingForHostCombatEnd
                    || Game.RealTimeCombat.CombatEndPending)) return false;
            _deferredEndCombat = Game.Combat;
            // A local membership callback is not authority to freeze the peer.
            // Keep receiving host states until its explicit final Prepare.
            return true; // Only the host can close an RTwP encounter.
        }

        private async void OnNotifyRealTimeCombatEnd(long receivedFrom, NotifyRealTimeCombatEnd frame)
        {
            if (!RealTimePresentationRuntime.Enabled || receivedFrom != NetworkConstants.HostPlayerId || frame == null
                || !Guid.TryParseExact(frame.EndId, "N", out _) || !Enum.IsDefined(typeof(RealTimeCombatEndStage), frame.Stage)
                || string.IsNullOrWhiteSpace(frame.SessionId) || Game?.Id != frame.SessionId
                || Game.LoadedSaveSeed != frame.LoadedSaveSeed) return;
            frame = ProtoBuf.Serializer.DeepClone(frame);
            await _realTimeSnapshotGate.WaitAsync();
            var session = Game;
            var combat = session?.Combat;
            var state = session?.RealTimeCombat;
            try
            {
                if (session?.Id != frame.SessionId || session.LoadedSaveSeed != frame.LoadedSaveSeed || state == null) return;
                var sameReceipt = ReferenceEquals(_endReceiptSession, session) && _endReceiptSave == frame.LoadedSaveSeed
                    && _endReceipt?.EndId == frame.EndId && _endReceipt.Epoch == frame.Epoch
                    && _endReceipt.CanonicalStateHash == frame.CanonicalStateHash;
                if (sameReceipt && frame.Stage <= _endAppliedStage)
                {
                    SendCombatEndAck(frame, true, frame.CanonicalStateHash, null);
                    return;
                }
                if (frame.Stage == (int)RealTimeCombatEndStage.Prepare)
                {
                    if (combat?.Stage != NetworkCombatStage.Running || frame.State == null
                        || (frame.Epoch <= state.Epoch && !(sameReceipt && _endAppliedStage == 0))
                        || state.Phase != CombatSynchronizationPhase.Stable) return;
                    _endReceiptSession = session; _endReceiptSave = frame.LoadedSaveSeed;
                    _endReceipt = ProtoBuf.Serializer.DeepClone(frame); _endAppliedStage = 0;
                    state.CombatEndPending = true;
                    state.Epoch = frame.Epoch; state.LastAuthoritativeSequence = 0;
                    await GameInteraction.SetPauseAsync(true);
                    if (!IsCurrentRealTimeMessage(session, state, frame.Epoch) || !ReferenceEquals(session.Combat, combat)) return;
                    var expected = Mapper.Map<NetworkCombatState>(frame.State);
                    var applied = await CombatInteraction.TryApplyAndReadRealTimeCombatStateAsync(expected,
                        () => IsCurrentRealTimeMessage(session, state, frame.Epoch) && ReferenceEquals(session.Combat, combat)
                            && session.LoadedSaveSeed == frame.LoadedSaveSeed && state.CombatEndPending);
                    if (!IsCurrentRealTimeMessage(session, state, frame.Epoch) || !ReferenceEquals(session.Combat, combat)
                        || session.LoadedSaveSeed != frame.LoadedSaveSeed || applied == null) return;
                    var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = applied });
                    if (hash != frame.CanonicalStateHash || RealTimeCombatTemporalDiff.Summarize(expected, applied) != null)
                    { SendCombatEndAck(frame, false, hash, "final-state-hash-mismatch"); return; }
                    _endReceiptSession = session; _endReceiptSave = frame.LoadedSaveSeed;
                    _endReceipt = ProtoBuf.Serializer.DeepClone(frame); _endAppliedStage = frame.Stage;
                    state.CanonicalStateHash = hash;
                    SendCombatEndAck(frame, true, hash, null);
                    return;
                }
                if (!sameReceipt || frame.Epoch != state.Epoch || !state.CombatEndPending
                    || state.Phase != CombatSynchronizationPhase.Stable
                    || combat == null || frame.Stage != _endAppliedStage + 1 || frame.State != null) return;
                if (frame.Stage == (int)RealTimeCombatEndStage.Finalize)
                {
                    if (!await CombatInteraction.TryFinalizeRealTimeCombatAsync(
                        () => IsCurrentRealTimeMessage(session, state, frame.Epoch) && ReferenceEquals(session.Combat, combat)
                            && session.LoadedSaveSeed == frame.LoadedSaveSeed && state.CombatEndPending,
                        () =>
                        {
                            CompleteCombatEnd();
                            state.CombatEndPending = false;
                            _deferredEndCombat = null;
                            _disconnectedDuringRealTimeCombat = false;
                            GameInteraction.SetPause(state.IsManualPauseActive || session.ForcedPause != null);
                        })) return;
                }
                _endAppliedStage = frame.Stage;
                SendCombatEndAck(frame, true, frame.CanonicalStateHash, null);
            }
            catch (Exception failure)
            {
                Logger.LogError(failure, "Unable to apply final host combat checkpoint. EndId={EndId}", frame.EndId);
                if (ReferenceEquals(Game, session) && session?.LoadedSaveSeed == frame.LoadedSaveSeed)
                    SendCombatEndAck(frame, false, 0, "final-checkpoint-application-failed");
            }
            finally { _realTimeSnapshotGate.Release(); }
        }

        private void SendCombatEndAck(NotifyRealTimeCombatEnd frame, bool applied, ulong hash, string reason) =>
            Send(new ClientRealTimeCombatEndAck
            {
                SessionId = frame.SessionId, LoadedSaveSeed = frame.LoadedSaveSeed, Epoch = frame.Epoch,
                EndId = frame.EndId, Stage = frame.Stage, CanonicalStateHash = hash, Applied = applied, Reason = reason
            });
    }
}
