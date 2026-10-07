using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services
{
    public static class RealTimeUnitResultState
    {
        public static bool IsValid(NetworkUnitVitals state) => state != null && state.Damage >= 0
            && state.LifeState >= 0 && state.LifeState <= 2
            && ValidStat(state.HitPoints) && ValidStat(state.TemporaryHitPoints) && ValidStat(state.NonLethal)
            && (!state.GroupCounterCaptured || state.GroupCombatCounter >= 0);

        private static bool ValidStat(NetworkStatValues state) => state != null
            && state.BaseValue >= 0 && state.ModifiedValue >= 0;

        public static string Validate(NetworkUnitDescriptor expected, NetworkUnitDescriptor actual)
        {
            if (expected == null || !IsValid(expected.Vitals) || expected.Damage != expected.Vitals.Damage
                || expected.Stats?.DamageNonLethal != expected.Vitals.NonLethal.BaseValue
                || expected.State?.IsFinallyDead != expected.Vitals.IsFinallyDead)
                return "invalid-unit-result-vitals";
            if (actual?.Vitals == null || expected.Vitals.GroupCounterCaptured && !actual.Vitals.GroupCounterCaptured)
                return "missing-unit-result-target";
            if (!expected.ResourcesCaptured || !expected.SpellbooksCaptured
                || !actual.ResourcesCaptured || !actual.SpellbooksCaptured)
                return "incomplete-unit-result";
            return RealTimeResourceState.Validate(expected.Resources, actual.Resources)
                ?? RealTimeSpellbookState.Validate(expected.Spellbooks, actual.Spellbooks);
        }
    }
}
