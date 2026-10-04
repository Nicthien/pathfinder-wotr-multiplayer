using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Controllers.Units;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Commands.Base;
using Kingmaker.Visual.Animation.Kingmaker;
using Kingmaker.Visual.Animation.Kingmaker.Actions;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Entities.Combat.RealTime;
using Microsoft.Extensions.Logging;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public class RealTimePresentationAttackPatches
    {
        private static long _nextActionId;
        private static readonly ConcurrentDictionary<UnitCommand, string> ActionIds = new();
        private static readonly ConcurrentDictionary<string, string> UnitActions = new(StringComparer.Ordinal);

        private static bool CanPublish => RealTimePresentationRuntime.Enabled
            && Main.Multiplayer?.IsRealTimeCombatActive == true
            && !Main.Multiplayer.IsRealTimeClientCombatActive
            && !Main.Multiplayer.IsRealTimeCombatBarrierActive;

        [HarmonyPatch(typeof(UnitCommand), "StartAnimation", new[] { typeof(UnitAnimationActionHandle) })]
        [HarmonyPostfix]
        public static void UnitCommand_StartAnimation_Postfix(UnitCommand __instance, UnitAnimationActionHandle __0)
        {
            if (!CanPublish || __instance is not UnitAttack attack || __0?.Action == null) return;
            var actionId = ActionIds.GetOrAdd(__instance, _ =>
                $"{attack.Executor?.UniqueId}:{Interlocked.Increment(ref _nextActionId)}");
            if (!string.IsNullOrWhiteSpace(attack.Executor?.UniqueId))
                UnitActions[attack.Executor.UniqueId] = actionId;
            Main.Multiplayer.PublishRealTimeAuthoritativeEvent(new NotifyRealTimeAuthoritativeEvent
            {
                ActionId = actionId,
                Kind = (int)RealTimeAuthoritativeEventKind.AttackPresentation,
                UnitId = attack.Executor?.UniqueId,
                TargetUnitId = attack.Target?.UniqueId,
                HostGameTimeTicks = Game.Instance.TimeController.GameTime.Ticks,
                Attack = new RealTimeAttackPresentationPayload
                {
                    Stage = (int)RealTimePresentationStage.Start,
                    AnimationType = (int)__0.Action.Type,
                    WeaponStyle = (int)__0.AttackWeaponStyle,
                    Variant = __0.Variant,
                    SpeedScale = __0.SpeedScale,
                    TargetDistance = __0.AttackTargetDistance,
                    IsCharge = __0.IsChargeAttack,
                    IsRend = __0.IsRendAttack,
                    IsMounted = __0.IsMounted,
                    SpecialAttackType = __0.Action is UnitAnimationActionSpecialAttack special ? (int)special.AttackType : 0
                }
            });
        }

        [HarmonyPatch(typeof(UnitCommand), "OnEnded", new[] { typeof(bool) })]
        [HarmonyPostfix]
        public static void UnitCommand_OnEnded_Postfix(UnitCommand __instance)
        {
            if (__instance is not UnitAttack attack || !ActionIds.TryRemove(__instance, out var actionId)) return;
            if (!string.IsNullOrWhiteSpace(attack.Executor?.UniqueId))
                UnitActions.TryRemove(attack.Executor.UniqueId, out _);
            if (!CanPublish) return;
            Main.Multiplayer.PublishRealTimeAuthoritativeEvent(new NotifyRealTimeAuthoritativeEvent
            {
                ActionId = actionId,
                Kind = (int)RealTimeAuthoritativeEventKind.AttackPresentation,
                UnitId = attack.Executor?.UniqueId,
                TargetUnitId = attack.Target?.UniqueId,
                HostGameTimeTicks = Game.Instance.TimeController.GameTime.Ticks,
                Attack = new RealTimeAttackPresentationPayload
                {
                    Stage = (int)(__instance.Result == UnitCommand.ResultType.Interrupt
                        ? RealTimePresentationStage.Interrupt : RealTimePresentationStage.End)
                }
            });
        }

        [HarmonyPatch(typeof(RuleAttackWithWeapon), nameof(RuleAttackWithWeapon.OnTrigger))]
        [HarmonyPostfix]
        public static void RuleAttackWithWeapon_OnTrigger_Postfix(RuleAttackWithWeapon __instance)
        {
            var unitId = __instance?.Initiator?.UniqueId;
            if (!CanPublish || string.IsNullOrWhiteSpace(unitId)
                || !UnitActions.TryGetValue(unitId, out var actionId)) return;
            Main.Multiplayer.PublishRealTimeAuthoritativeEvent(new NotifyRealTimeAuthoritativeEvent
            {
                ActionId = actionId,
                Kind = (int)RealTimeAuthoritativeEventKind.AttackPresentation,
                UnitId = unitId,
                TargetUnitId = __instance.Target?.UniqueId,
                HostGameTimeTicks = Game.Instance.TimeController.GameTime.Ticks,
                Attack = new RealTimeAttackPresentationPayload
                {
                    Stage = (int)RealTimePresentationStage.Impact
                }
            });
        }
    }

    [HarmonyPatch]
    public class RealTimePresentationControllerPatches
    {
        // Deliberately leave UnitAnimationController and camera/UI controllers running.
        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> Targets()
        {
            foreach (var type in new[] { typeof(UnitCommandController), typeof(UnitBuffsController) })
                yield return AccessTools.DeclaredMethod(type, "TickOnUnit", new[] { typeof(UnitEntityData) })
                    ?? throw new MissingMethodException(type.FullName, "TickOnUnit");
            yield return AccessTools.DeclaredMethod(typeof(UnitMoveController), "TickUnit", new[] { typeof(UnitEntityData) })
                ?? throw new MissingMethodException(typeof(UnitMoveController).FullName, "TickUnit");
        }

        [HarmonyPrefix]
        public static bool BeforeTick(UnitEntityData __0) =>
            !RealTimePresentationRuntime.IsClientActive || __0?.IsInCombat != true;
    }

    [HarmonyPatch]
    public class RealTimePresentationRulePatches
    {
        private static readonly string[] MutationPrefixes =
            { "RuleAttack", "RuleDeal", "RuleHeal", "RuleCast", "RuleDrain", "RuleCombatManeuver", "RuleSummon" };
        public static bool IsMutation(Type type) => MutationPrefixes.Any(prefix => type.Name.StartsWith(prefix, StringComparison.Ordinal));

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> Targets() => typeof(RulebookEvent).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(RulebookEvent).IsAssignableFrom(type) && IsMutation(type))
            .Select(type => AccessTools.DeclaredMethod(type, nameof(RulebookEvent.OnTrigger)))
            .Where(method => method != null && !method.IsAbstract);

        [HarmonyPrefix]
        public static bool BeforeRule(RulebookEvent __instance)
        {
            if (!RealTimePresentationRuntime.IsClientActive) return true;
            Main.GetLogger<RealTimePresentationRulePatches>().LogError(
                "Blocked autonomous client combat rule in presentation prototype. RuleType={RuleType}, UnitId={UnitId}",
                __instance.GetType().Name, __instance.Initiator?.UniqueId);
            Main.Multiplayer.ReportRealTimeAIActionFault(__instance.Initiator?.UniqueId, null,
                "autonomous-client-rule:" + __instance.GetType().Name);
            return false;
        }
    }
}
