using System;
using System.Threading.Tasks;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerHost
    {
        // Engine boundary; protocol tests replace capture, never the transfer or its guards.
        internal Func<string, string, Func<bool>, Task<RealTimeMemorySaver>> NativeCheckpointCapture;
        // Capture capability for the native reconstruction barrier. Not wired to
        // a user button or the old snapshot repair: a transfer/load/ACK protocol
        // must consume it before any mechanical repair obligation is cleared.
        internal Task<RealTimeMemorySaver> CaptureCurrentNativeCheckpointAsync(string checkpointId, Func<bool> barrierOwned)
        {
            if (barrierOwned == null) throw new ArgumentNullException(nameof(barrierOwned));
            var session = Game;
            var state = session?.RealTimeCombat;
            var encounter = session?.Combat;
            var id = session?.Id;
            var save = session?.LoadedSaveSeed;
            var epoch = state?.Epoch;
            var sequence = state?.LastAuthoritativeSequence;
            var phase = state?.Phase;
            bool Authorized()
            {
                lock (ActionLock)
                    return RealTimePresentationRuntime.Enabled && IsActive && barrierOwned()
                        && session != null && state != null && encounter != null
                        && ReferenceEquals(Game, session) && ReferenceEquals(Game.RealTimeCombat, state)
                        && ReferenceEquals(Game.Combat, encounter) && Game.Id == id && Game.LoadedSaveSeed == save
                        && state.Epoch == epoch && state.LastAuthoritativeSequence == sequence && state.Phase == phase
                        && state.Mode == CombatMode.RealTimeWithPause && phase != CombatSynchronizationPhase.Stable;
            }
            return NativeCheckpointCapture != null
                ? NativeCheckpointCapture(id, checkpointId, Authorized)
                : RealTimeNativeHostCapture.CaptureAsync(_mainThreadAccessor, CombatInteraction, id, checkpointId, Authorized);
        }
    }
}
