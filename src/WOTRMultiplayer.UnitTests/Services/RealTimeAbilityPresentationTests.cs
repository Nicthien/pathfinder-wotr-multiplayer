using System;
using Kingmaker.Visual.Animation.Kingmaker;
using Kingmaker.Visual.Animation.Kingmaker.Actions;
using Kingmaker.View.Animation;
using NUnit.Framework;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeAbilityPresentationTests
    {
        private static RealTimeAbilityPresentationPayload Payload() => new()
        {
            Stage = 1, BlueprintId = "0123456789abcdef0123456789abcdef",
            AnimationType = (int)UnitAnimationType.CastSpell,
            CastStyle = (int)UnitAnimationActionCastSpell.CastAnimationStyle.Directional,
            SpecialBehaviour = (int)UnitAnimationActionCastSpell.SpecialBehaviourType.None,
            WeaponStyle = (int)WeaponAnimationStyle.None, SpeedScale = 1, CastingTime = 2, TargetDistance = 6
        };

        [Test]
        public void OrdinaryCastParameters_AreAccepted()
        {
            Assert.That(RealTimeAuthoritativePresentation.IsValidAbilityStart(Payload()), Is.True);
        }

        [Test]
        public void CastAndProjectileHookTargets_ExistInInstalledAssembly()
        {
            Assert.That(HarmonyLib.AccessTools.DeclaredMethod(typeof(Kingmaker.RuleSystem.Rules.Abilities.RuleCastSpell),
                "OnTrigger"), Is.Not.Null);
            Assert.That(HarmonyLib.AccessTools.DeclaredMethod(typeof(Kingmaker.Controllers.Projectiles.Projectile),
                "OnHit", Type.EmptyTypes), Is.Not.Null);
        }

        [TestCase("blueprint")]
        [TestCase("animation")]
        [TestCase("style")]
        [TestCase("behaviour")]
        [TestCase("weapon")]
        [TestCase("speed")]
        [TestCase("time")]
        [TestCase("distance")]
        [TestCase("transition")]
        public void MalformedCastParameters_AreRejected(string invalid)
        {
            var payload = Payload();
            switch (invalid)
            {
                case "blueprint": payload.BlueprintId = "not-an-asset"; break;
                case "animation": payload.AnimationType = (int)UnitAnimationType.MainHandAttack; break;
                case "style": payload.CastStyle = int.MaxValue; break;
                case "behaviour": payload.SpecialBehaviour = int.MaxValue; break;
                case "weapon": payload.WeaponStyle = int.MaxValue; break;
                case "speed": payload.SpeedScale = 0; break;
                case "time": payload.CastingTime = float.NaN; break;
                case "distance": payload.TargetDistance = float.PositiveInfinity; break;
                case "transition": payload.MinimalTransitionOut = -1; break;
            }
            Assert.That(RealTimeAuthoritativePresentation.IsValidAbilityStart(payload), Is.False);
        }

        [Test]
        public void UnknownOrAmbiguousPayload_IsNotSupported()
        {
            var frame = new NotifyRealTimeAuthoritativeEvent
            {
                UnitId = "hero", ActionId = "cast",
                Kind = (int)RealTimeAuthoritativeEventKind.AbilityPresentation, Ability = Payload()
            };
            Assert.That(RealTimeAuthoritativePresentation.IsSupported(frame), Is.True);
            frame.Attack = new RealTimeAttackPresentationPayload();
            Assert.That(RealTimeAuthoritativePresentation.IsSupported(frame), Is.False);
            frame.Attack = null;
            frame.Kind = 999;
            Assert.That(RealTimeAuthoritativePresentation.IsSupported(frame), Is.False);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void MissingActionOrUnitIdentity_IsRejected(bool unit)
        {
            var frame = new NotifyRealTimeAuthoritativeEvent
            {
                UnitId = unit ? null : "hero", ActionId = unit ? "cast" : " ",
                Kind = (int)RealTimeAuthoritativeEventKind.AbilityPresentation, Ability = Payload()
            };
            Assert.That(RealTimeAuthoritativePresentation.IsSupported(frame), Is.False);
        }

        [Test]
        public void CastLifecycle_UsesOneOrderedLaneWithAttacksAndImmutablePayloads()
        {
            var inbox = new RealTimePresentationInbox();
            var frame = new NotifyRealTimeAuthoritativeEvent
            {
                Sequence = 2, EventId = "1:2", ActionId = "cast",
                Kind = (int)RealTimeAuthoritativeEventKind.AbilityPresentation, Ability = Payload()
            };
            Assert.That(inbox.Enqueue(frame), Is.True);
            frame.Ability.CastingTime = 900;
            inbox.Enqueue(new NotifyRealTimeAuthoritativeEvent
            {
                Sequence = 1, EventId = "1:1", ActionId = "attack",
                Kind = (int)RealTimeAuthoritativeEventKind.AttackPresentation,
                Attack = new RealTimeAttackPresentationPayload { Stage = 1 }
            });
            Assert.That(inbox.TryPeek(out var first), Is.True);
            Assert.That(first.ActionId, Is.EqualTo("attack"));
            inbox.Commit(first.Sequence);
            Assert.That(inbox.TryPeek(out var cast), Is.True);
            Assert.That(cast.Ability.CastingTime, Is.EqualTo(2));
            Assert.That(cast.ActionId, Is.EqualTo("cast"));
            var conflicting = ProtoBuf.Serializer.DeepClone(cast);
            conflicting.Ability.BlueprintId = Guid.NewGuid().ToString("N");
            Assert.That(inbox.Enqueue(conflicting), Is.False);
        }
    }
}
