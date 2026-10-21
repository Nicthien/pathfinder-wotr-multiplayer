namespace WOTRMultiplayer.Services.CombatSaving
{
    internal sealed class CombatSaveEligibility
    {
        public bool Host, TurnBased, Running, PlayerTurn, Initialized;
        public bool ActionPending, Transition, Repair, Dialog, Cutscene, Loading, Rejoining, Divergent;
        public string Refusal()
        {
            if (!Host) return "host-only";
            if (!TurnBased || !Running) return "turn-based-combat-required";
            if (Divergent || Repair) return "synchronization-required";
            if (Transition || Loading || Rejoining) return "transition-in-progress";
            if (Dialog || Cutscene) return "scripted-sequence";
            if (!PlayerTurn || !Initialized) return "player-turn-required";
            if (ActionPending) return "action-in-progress";
            return null;
        }
    }
}
