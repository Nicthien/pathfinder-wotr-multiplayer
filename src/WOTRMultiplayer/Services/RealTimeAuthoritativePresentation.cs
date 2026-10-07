using System;
using Kingmaker.Visual.Animation.Kingmaker;
using Kingmaker.Visual.Animation.Kingmaker.Actions;
using Kingmaker.View.Animation;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Entities.Combat.RealTime;

namespace WOTRMultiplayer.Services
{
    public static class RealTimeAuthoritativePresentation
    {
        public static bool IsValidAbilityStart(RealTimeAbilityPresentationPayload ability) => ability != null
            && ability.Stage == (int)RealTimePresentationStage.Start && Guid.TryParse(ability.BlueprintId, out _)
            && ability.AnimationType == (int)UnitAnimationType.CastSpell
            && Enum.IsDefined(typeof(UnitAnimationActionCastSpell.CastAnimationStyle), ability.CastStyle)
            && Enum.IsDefined(typeof(UnitAnimationActionCastSpell.SpecialBehaviourType), ability.SpecialBehaviour)
            && Enum.IsDefined(typeof(WeaponAnimationStyle), ability.WeaponStyle)
            && FiniteNonnegative(ability.CastingTime) && FiniteNonnegative(ability.TargetDistance)
            && FiniteNonnegative(ability.MinimalTransitionOut) && FiniteNonnegative(ability.SpeedScale)
            && ability.SpeedScale > 0 && ability.SpeedScale <= 20;

        private static bool FiniteNonnegative(float value) => !float.IsNaN(value)
            && !float.IsInfinity(value) && value >= 0;

        // Do not accept ambiguous envelopes or dispatch an unknown payload as
        // another kind. Numeric/game-asset validation occurs on the game thread.
        public static bool IsSupported(NotifyRealTimeAuthoritativeEvent frame) => frame != null
            && !string.IsNullOrWhiteSpace(frame.ActionId)
            && ((!string.IsNullOrWhiteSpace(frame.UnitId) && frame.Projectile == null
                && ((frame.Kind == (int)RealTimeAuthoritativeEventKind.AttackPresentation
                    && frame.Attack != null && frame.Ability == null)
                || (frame.Kind == (int)RealTimeAuthoritativeEventKind.AbilityPresentation
                    && frame.Ability != null && frame.Attack == null)))
                || frame.Kind == (int)RealTimeAuthoritativeEventKind.ProjectilePresentation
                    && frame.Attack == null && frame.Ability == null && frame.Projectile?.State != null
                    && frame.Projectile.State.Id == frame.ActionId
                    && frame.Projectile.Stage >= 1 && frame.Projectile.Stage <= 3);
    }
}
