using System;
using System.Reflection;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules;
using NUnit.Framework;
using WOTRMultiplayer.HarmonyPatches.Combat;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeRuleDispatchTests
    {
        private sealed class RuleAttackProbe : RulebookEvent
        {
            public RuleAttackProbe() : base(null) { }
            public override void OnTrigger(RulebookEventContext context) => throw new AssertionException("Do not execute rules in a gate test.");
        }

        [TestCase(typeof(RuleAttackWithWeapon), true)]
        [TestCase(typeof(RuleRollD20), true)]
        [TestCase(typeof(RuleSavingThrow), true)]
        [TestCase(typeof(RuleSkillCheck), true)]
        [TestCase(typeof(RuleSpellResistanceCheck), true)]
        [TestCase(typeof(RuleDispelMagic), true)]
        [TestCase(typeof(Kingmaker.RuleSystem.Rules.Damage.RuleCalculateDamage), true)]
        [TestCase(typeof(Kingmaker.RuleSystem.Rules.Abilities.RuleApplySpell), true)]
        [TestCase(typeof(Kingmaker.RuleSystem.Rules.Abilities.RuleCheckConcentration), true)]
        [TestCase(typeof(RuleCalculateAC), false)]
        [TestCase(typeof(string), false)]
        public void HostOnlyRules_IncludeMutationsAndRandomChecksButNotDisplayQueries(Type type, bool blocked) =>
            Assert.That(RealTimeRuleDispatchPatches.IsHostOnly(type), Is.EqualTo(blocked));

        [Test]
        public void EveryCallbackOfOneRejectedEvent_IsSuppressedAndReportsOnlyOnce()
        {
            var rule = new RuleAttackProbe();
            var reports = 0;
            Action<RuleAttackProbe> action = _ => throw new AssertionException("Must never invoke the callback while preparing cleanup.");
            for (var stage = 0; stage < 5; stage++)
                Assert.That(RealTimeRuleDispatchPatches.BeforeCallback(rule, action, true, _ => reports++), Is.False);
            Assert.That(reports, Is.EqualTo(1));
            Assert.That(rule.IsTriggered, Is.False);
            // The native finally must be safe even before the first BeforeEvent.
            var holder = typeof(Rulebook).GetNestedType("EventHandler`1", BindingFlags.NonPublic).MakeGenericType(typeof(RuleAttackProbe));
            Assert.DoesNotThrow(() => holder.GetMethod("CleanUp").Invoke(null, null));
        }

        [Test]
        public void StaticBaseDispatchType_IsTakenFromTheDelegateNotTheConcreteRule()
        {
            var rule = new RuleAttackProbe();
            Action<RulebookEvent> callback = _ => throw new AssertionException("Must not invoke callback.");
            Assert.That(RealTimeRuleDispatchPatches.BeforeCallback(rule, callback, true, null), Is.False);
            var holder = typeof(Rulebook).GetNestedType("EventHandler`1", BindingFlags.NonPublic).MakeGenericType(typeof(RulebookEvent));
            Assert.DoesNotThrow(() => holder.GetMethod("CleanUp").Invoke(null, null));
        }

        [Test]
        public void HostOrInactivePrototype_LeavesCallbackUntouchedWithoutReporting()
        {
            Assert.That(RealTimeRuleDispatchPatches.BeforeCallback(new RuleAttackProbe(), null, false,
                _ => throw new AssertionException("Must not report host rules.")), Is.True);
        }

        [Test]
        public void AuthoritativePresentationStillSuppressesCallbacksWithoutReportingAFault()
        {
            var rule = new RuleAttackProbe();
            var reports = 0;
            Action<RuleAttackProbe> action = _ => throw new AssertionException("Presentation must not execute rules.");
            using (RealTimePresentationRuntime.BeginAuthoritativePresentation())
            {
                Assert.That(RealTimeRuleDispatchPatches.BeforeCallback(rule, action, true, _ => reports++), Is.False);
                Assert.That(RealTimePresentationRuntime.IsApplyingAuthoritativePresentation, Is.True);
            }
            Assert.That(reports, Is.Zero);
            Assert.That(RealTimePresentationRuntime.IsApplyingAuthoritativePresentation, Is.False);
            var holder = typeof(Rulebook).GetNestedType("EventHandler`1", BindingFlags.NonPublic)
                .MakeGenericType(typeof(RuleAttackProbe));
            Assert.DoesNotThrow(() => holder.GetMethod("CleanUp").Invoke(null, null));
        }

        [Test]
        public void MalformedCallback_FailsBeforeClaimingItWasSuppressed()
        {
            var reports = 0;
            Assert.Throws<InvalidOperationException>(() => RealTimeRuleDispatchPatches.BeforeCallback(
                new RuleAttackProbe(), (Action)(() => { }), true, _ => reports++));
            Assert.That(reports, Is.Zero);
        }

        [Test]
        public void PatchIsPrototypeOnlyAndTargetsCallbackExecutionRatherThanTypedDispatch()
        {
            Assert.That(RealTimeRuleDispatchPatches.Prepare(), Is.EqualTo(RealTimePresentationRuntime.Enabled));
            var target = (MethodInfo)RealTimeRuleDispatchPatches.Target();
            Assert.That(target.Name, Is.EqualTo("SaveExecute"));
            Assert.That(target.IsStatic, Is.True);
            Assert.That(target.ContainsGenericParameters, Is.False);
            Assert.That(target.ReturnType, Is.EqualTo(typeof(void)));
            Assert.That(target.GetParameters()[1].ParameterType, Is.EqualTo(typeof(Action<RulebookEvent>)));
            Assert.That(RealTimeRuleDispatchPatches.IsSupportedModule(target.Module.ModuleVersionId), Is.True);
            Assert.That(RealTimeRuleDispatchPatches.IsSupportedModule(Guid.Empty), Is.False);
        }
    }
}
