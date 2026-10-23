using System.Collections.Generic;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Entities.Combat
{
    public class NetworkCombatState
    {
        public int RoundNumber { get; set; }

        public bool HasSurpriseRound { get; set; }

        public List<NetworkUnit> Units { get; set; } = [];

        public List<NetworkAreaEffect> AreaEffects { get; set; } = [];

        public bool ProjectilesCaptured { get; set; }
        public Networking.Messages.Contracts.NetworkNativeTopology NativeTopology { get; set; }
        public Networking.Messages.Contracts.NetworkAcceptedOrderReference AcceptedOrderReference { get; set; }

        // Native checkpoint only: minimal authoritative buff inventories for
        // every unit in the loaded world, including non-combatants. These are
        // reconstruction material, not encounter participants, and therefore
        // are deliberately excluded from the regular RT temporal hash.
        public List<NetworkUnit> NativeUnits { get; set; } = [];

        public List<RealTime.NetworkProjectileState> Projectiles { get; set; } = [];
    }
}
