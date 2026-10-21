using System;
using System.Collections.Concurrent;
using System.Threading;
using HarmonyLib;
using Kingmaker;
using Kingmaker.RuleSystem.Rules.Abilities;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Commands.Base;
using Kingmaker.Visual.Animation.Kingmaker;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public static class RealTimeAbilityPresentationPatches
    {
        private static long _nextActionId;
        private static readonly ConcurrentDictionary<UnitCommand, string> Actions = new();
        private static readonly ConcurrentDictionary<string, string> UnitActions = new(StringComparer.Ordinal);
        internal static bool IsCurrentAction(string unitId, string actionId) => unitId != null
            && UnitActions.TryGetValue(unitId, out var current) && current == actionId;
        private static bool CanPublish => RealTimePresentationRuntime.Enabled
            && Main.Multiplayer?.IsRealTimeCombatActive == true
            && !Main.Multiplayer.IsRealTimeClientCombatActive
            && !Main.Multiplayer.IsRealTimeCombatBarrierActive;

        public static void AfterStartAnimation(UnitCommand __instance, UnitAnimationActionHandle __0)
        {
            if (!CanPublish || __instance is not UnitUseAbility command || __0?.Action == null) return;
            var actionId = Actions.GetOrAdd(command, _ =>
                $"{command.Executor?.UniqueId}:ability:{Interlocked.Increment(ref _nextActionId)}");
            if (!string.IsNullOrWhiteSpace(command.Executor?.UniqueId)) UnitActions[command.Executor.UniqueId] = actionId;
            Publish(command, actionId, new RealTimeAbilityPresentationPayload
            {
                Stage = (int)RealTimePresentationStage.Start,
                BlueprintId = command.Ability?.Blueprint?.AssetGuid.ToString(),
                AnimationType = (int)__0.Action.Type, CastStyle = (int)__0.CastStyle,
                SpecialBehaviour = (int)__0.SpecialCastBehaviour, CastingTime = __0.CastingTime,
                SpeedScale = __0.SpeedScale, WeaponStyle = (int)__0.AttackWeaponStyle,
                TargetDistance = __0.AttackTargetDistance, IsMounted = __0.IsMounted,
                MinimalTransitionOut = __0.MinimalTransitionOut
            });
        }

        public static void AfterEnded(UnitCommand __instance)
        {
            if (__instance is not UnitUseAbility command || !Actions.TryRemove(command, out var actionId)) return;
            var unitId = command.Executor?.UniqueId;
            if (!string.IsNullOrWhiteSpace(unitId) && UnitActions.TryGetValue(unitId, out var current)
                && current == actionId) UnitActions.TryRemove(unitId, out _);
            if (CanPublish) Publish(command, actionId, new RealTimeAbilityPresentationPayload
            {
                Stage = (int)(command.Result == UnitCommand.ResultType.Interrupt
                    || command.Result == UnitCommand.ResultType.Fail ? RealTimePresentationStage.Interrupt : RealTimePresentationStage.End)
            });
        }

        [HarmonyPatch(typeof(RuleCastSpell), nameof(RuleCastSpell.OnTrigger))]
        [HarmonyPostfix]
        public static void RuleCastSpell_OnTrigger_Postfix(RuleCastSpell __instance)
        {
            var unitId = __instance?.Initiator?.UniqueId;
            if (!CanPublish || __instance?.Success != true || string.IsNullOrWhiteSpace(unitId)
                || !UnitActions.TryGetValue(unitId, out var actionId)) return;
            Main.Multiplayer.PublishRealTimeAuthoritativeEvent(new NotifyRealTimeAuthoritativeEvent
            {
                ActionId = actionId, UnitId = unitId,
                Kind = (int)RealTimeAuthoritativeEventKind.AbilityPresentation,
                HostGameTimeTicks = Game.Instance.TimeController.GameTime.Ticks,
                // Cast release, not projectile collision or damage. No client
                // execution context or gameplay callback is produced by this.
                Ability = new RealTimeAbilityPresentationPayload { Stage = (int)RealTimePresentationStage.Impact }
            });
        }

        private static void Publish(UnitUseAbility command, string actionId, RealTimeAbilityPresentationPayload payload) =>
            Main.Multiplayer.PublishRealTimeAuthoritativeEvent(new NotifyRealTimeAuthoritativeEvent
            {
                ActionId = actionId, UnitId = command.Executor?.UniqueId,
                TargetUnitId = command.Target?.Unit?.UniqueId,
                Kind = (int)RealTimeAuthoritativeEventKind.AbilityPresentation,
                HostGameTimeTicks = Game.Instance.TimeController.GameTime.Ticks, Ability = payload
            });
    }
}
