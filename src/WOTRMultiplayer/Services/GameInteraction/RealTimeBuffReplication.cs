using System;
using Kingmaker.UnitLogic.Buffs;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Extensions;

namespace WOTRMultiplayer.Services.GameInteraction
{
    public static class RealTimeBuffReplication
    {
        // Caller preflights every instance before any unit is changed.
        // No SetRank/Activate/Deactivate/SetDuration or component callback.
        public static void ApplyValues(Buff buff, NetworkBuff state, TimeSpan baseTime)
        {
            if (buff == null || state == null || state.Rank < 0 || state.TimeLeft < TimeSpan.Zero)
                throw new InvalidOperationException("invalid-buff-values");
            buff.Rank = state.Rank;
            buff.EndTime = state.IsPermanent ? TimeSpan.MaxValue : baseTime.SafeAdd(state.TimeLeft);
            buff.NextTickTime = state.NextTickTime == TimeSpan.MaxValue ? TimeSpan.MaxValue : baseTime.SafeAdd(state.NextTickTime);
            buff.NextResourceSpendingTime = state.NextResourceSpendingTime == TimeSpan.MaxValue
                ? TimeSpan.MaxValue : baseTime.SafeAdd(state.NextResourceSpendingTime);
        }
    }
}
