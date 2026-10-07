using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;
using Kingmaker.RuleSystem;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public static class RealTimeRuleDispatchPatches
    {
        private sealed class Rejection { public bool Reported; }
        private static readonly ConditionalWeakTable<RulebookEvent, Rejection> Rejections = new();
        private static readonly ConcurrentDictionary<Type, MethodInfo> HandlerInitializers = new();
        private static readonly HashSet<string> DisplayQueries = new(StringComparer.Ordinal)
        {
            "RuleCalculateAC", "RuleCalculateArmorCheckPenalty", "RuleCalculateArmorMaxDexBonusLimit",
            "RuleCalculateAttackBonusWithoutTarget", "RuleCalculateAttackBonus", "RuleCalculateAttacksCount",
            "RuleCalculateAttacksCountLimbs", "RuleCalculateBaseCMB", "RuleCalculateCMB", "RuleCalculateBaseCMD",
            "RuleCalculateCMD", "RuleCalculateWeaponSizeBonus", "RuleCalculateWeaponStats", "RuleCanApplyBuff",
            "RuleCheckTargetFlatFooted", "RuleCollectMetamagic", "RuleCalculateAbilityParams",
            "RuleCalculateArcaneSpellFailureChance"
        };

        // Never patch TriggerEventInternal<T>: closing that shared body over
        // RulebookEvent would change the host's typed handler dispatch. SaveExecute
        // only invokes the already-created Action<T>; that delegate retains its T.
        [HarmonyPrepare]
        public static bool Prepare() => RealTimePresentationRuntime.Enabled;

        [HarmonyTargetMethod]
        public static MethodBase Target()
        {
            var method = typeof(Rulebook).GetMethod("SaveExecute", BindingFlags.Static | BindingFlags.NonPublic)
                ?.MakeGenericMethod(typeof(RulebookEvent))
                ?? throw new MissingMethodException(typeof(Rulebook).FullName, "SaveExecute");
            // The proof is for this exact engine body/module, not arbitrary game
            // updates. In particular the host body must not reify T itself.
            if (!IsSupportedModule(method.Module.ModuleVersionId))
                throw new NotSupportedException("rtwp-rule-dispatch-game-module-unverified");
            using var hash = SHA256.Create();
            var actual = BitConverter.ToString(hash.ComputeHash(method.GetMethodBody().GetILAsByteArray())).Replace("-", "");
            if (actual != "0ADE0D9A2A12100868F3539A47AE0A2A5742A75E0B74B5E469EF416F017123BD")
                throw new NotSupportedException("rtwp-rule-dispatch-game-body-unverified");
            return method;
        }

        public static bool IsSupportedModule(Guid id) => id == new Guid("90a9869c-2792-4c7b-bfb7-5a8b33da7c82");

        public static bool IsHostOnly(Type type)
        {
            if (type == null || !typeof(RulebookEvent).IsAssignableFrom(type)) return false;
            // Unknown rule types are not silently assumed to be display queries.
            // Never allow random damage calculation just because it is named Calculate.
            return type.Assembly != typeof(RulebookEvent).Assembly || !DisplayQueries.Contains(type.Name);
        }

        // Kept independent of Main so the exact callback gate can be exercised
        // with the installed Rulebook/Harmony/Mono, without starting the game.
        public static bool BeforeCallback(RulebookEvent rule, Delegate callback, bool clientActive,
            Action<RulebookEvent> report)
        {
            if (!clientActive || rule == null || !IsHostOnly(rule.GetType())) return true;
            var callbackType = callback?.GetType();
            if (callbackType == null || !callbackType.IsGenericType
                || callbackType.GetGenericTypeDefinition() != typeof(Action<>))
                throw new InvalidOperationException("unsupported-rule-callback");
            var dispatchType = callbackType.GetGenericArguments()[0];
            if (!typeof(RulebookEvent).IsAssignableFrom(dispatchType) || !dispatchType.IsInstanceOfType(rule))
                throw new InvalidOperationException("conflicting-rule-callback-type");

            // TriggerEventInternal finally calls EventHandler<T>.CleanUp, even if
            // every callback is suppressed. Initialize the holder without invoking
            // EventBus, so that finally is safe on the very first blocked event.
            var initializer = HandlerInitializers.GetOrAdd(dispatchType, type =>
            {
                var handler = typeof(Rulebook).GetNestedType("EventHandler`1", BindingFlags.NonPublic)
                    ?? throw new MissingMemberException(typeof(Rulebook).FullName, "EventHandler<T>");
                return handler.MakeGenericType(type).GetMethod("BeforeEvent", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingMethodException(handler.FullName, "BeforeEvent");
            });
            initializer.Invoke(null, new object[] { rule });
            var rejection = Rejections.GetValue(rule, _ => new Rejection());
            if (!rejection.Reported)
            {
                rejection.Reported = true;
                report?.Invoke(rule);
            }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool BeforeDispatch(RulebookEvent __0, Delegate __1) =>
            BeforeCallback(__0, __1, RealTimePresentationRuntime.IsClientActive, Report);

        private static void Report(RulebookEvent rule) => Main.Multiplayer.ReportRealTimeAIActionFault(
            rule.Initiator?.UniqueId, null, "autonomous-client-rule-dispatch:" + rule.GetType().Name);
    }
}
