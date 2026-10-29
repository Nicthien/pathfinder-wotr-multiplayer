using System.Collections.Generic;

namespace WOTRMultiplayer.Entities.Combat.RealTime
{
    public class RealTimeCombatSnapshot
    {
        public NetworkCombatState Combat { get; set; } = new NetworkCombatState();

        public long RandomState { get; set; }

        public List<RealTimeCommandEnvelope> ActiveOrders { get; set; } = [];
    }
}
