using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    [TestFixture]
    public class RealTimeCombatHasherTests
    {
        [Test]
        public void OrderOfUnitsBuffsAndAreas_DoesNotAffectHash()
        {
            var first = Sample();
            var second = Sample();
            second.Combat.Units.Reverse();
            second.Combat.Units[1].BuffCollection.Buffs.Reverse();
            second.Combat.AreaEffects.Reverse();

            Assert.That(RealTimeCombatHasher.Compute(second), Is.EqualTo(RealTimeCombatHasher.Compute(first)));
        }

        [Test]
        public void PositionDamageBuffAndRandomDivergence_ChangeHash()
        {
            var baseline = RealTimeCombatHasher.Compute(Sample());
            var moved = Sample();
            moved.Combat.Units[0].Position.X += 0.1f;
            var hurt = Sample();
            hurt.Combat.Units[0].Descriptor.Damage++;
            var buffed = Sample();
            buffed.Combat.Units[0].BuffCollection.Buffs[0].Rank++;
            var random = Sample();
            random.RandomState++;

            Assert.That(RealTimeCombatHasher.Compute(moved), Is.Not.EqualTo(baseline));
            Assert.That(RealTimeCombatHasher.Compute(hurt), Is.Not.EqualTo(baseline));
            Assert.That(RealTimeCombatHasher.Compute(buffed), Is.Not.EqualTo(baseline));
            Assert.That(RealTimeCombatHasher.Compute(random), Is.Not.EqualTo(baseline));
        }

        [Test]
        public void NonLethalDamageDivergence_ChangesHash()
        {
            var baseline = RealTimeCombatHasher.Compute(Sample());
            var unconscious = Sample();
            unconscious.Combat.Units[0].Descriptor.Stats = new NetworkCharacterStats
            {
                DamageNonLethal = 7
            };

            Assert.That(RealTimeCombatHasher.Compute(unconscious), Is.Not.EqualTo(baseline));
        }

        [Test]
        public void UnitAndAreaBlueprintDivergence_ChangeHash()
        {
            var baseline = RealTimeCombatHasher.Compute(Sample());
            var changedUnit = Sample();
            changedUnit.Combat.Units[0].BlueprintId = "different-unit-blueprint";
            var changedArea = Sample();
            changedArea.Combat.AreaEffects[0].BlueprintId = "different-area-blueprint";

            Assert.That(RealTimeCombatHasher.Compute(changedUnit), Is.Not.EqualTo(baseline));
            Assert.That(RealTimeCombatHasher.Compute(changedArea), Is.Not.EqualTo(baseline));
        }

        [Test]
        public void AreaEffectContextDivergence_ChangesHash()
        {
            var baseline = RealTimeCombatHasher.Compute(Sample());
            var changed = Sample();
            changed.Combat.AreaEffects[0].CasterUnitId = "different-caster";

            Assert.That(RealTimeCombatHasher.Compute(changed), Is.Not.EqualTo(baseline));
        }

        [Test]
        public void AreaAndActiveOrderDivergence_ChangeHash()
        {
            var baseline = RealTimeCombatHasher.Compute(Sample());
            var area = Sample();
            area.Combat.AreaEffects[0].Position = new NetworkVector3(2, 0, 0);
            var order = Sample();
            order.ActiveOrders.Add(new RealTimeCommandEnvelope { Epoch = 1, Sequence = 1, PlayerId = 42, UnitId = "a", Kind = RealTimeCommandKind.Attack });

            Assert.That(RealTimeCombatHasher.Compute(area), Is.Not.EqualTo(baseline));
            Assert.That(RealTimeCombatHasher.Compute(order), Is.Not.EqualTo(baseline));
        }

        [Test]
        public void ProneAndEngagementDivergence_ChangeHash()
        {
            var baseline = RealTimeCombatHasher.Compute(Sample());
            var prone = Sample();
            prone.Combat.Units[0].Descriptor.State = new NetworkUnitState
            {
                Prone = new NetworkUnitProneState { Active = true }
            };
            var engagement = Sample();
            engagement.Combat.Units[0].CombatState = new NetworkUnitCombatState
            {
                EngagedUnits = [new NetworkUnitEngagement { UnitId = "d", Rounds = 2 }]
            };

            Assert.That(RealTimeCombatHasher.Compute(prone), Is.Not.EqualTo(baseline));
            Assert.That(RealTimeCombatHasher.Compute(engagement), Is.Not.EqualTo(baseline));
        }

        [Test]
        public void EngagementOrder_DoesNotAffectHash()
        {
            var first = Sample();
            first.Combat.Units[0].CombatState = new NetworkUnitCombatState
            {
                EngagedUnits =
                [
                    new NetworkUnitEngagement { UnitId = "d", Rounds = 2 },
                    new NetworkUnitEngagement { UnitId = "e", Rounds = 1 }
                ]
            };
            var second = Sample();
            second.Combat.Units[0].CombatState = new NetworkUnitCombatState
            {
                EngagedUnits =
                [
                    new NetworkUnitEngagement { UnitId = "e", Rounds = 1 },
                    new NetworkUnitEngagement { UnitId = "d", Rounds = 2 }
                ]
            };

            Assert.That(RealTimeCombatHasher.Compute(second), Is.EqualTo(RealTimeCombatHasher.Compute(first)));
        }

        [Test]
        public void LocalEngagementRoundTiming_DoesNotCauseFalseDivergence()
        {
            var first = Sample();
            first.Combat.Units[0].CombatState = new NetworkUnitCombatState
            {
                EngagedUnits = [new NetworkUnitEngagement { UnitId = "d", Rounds = 2 }]
            };
            var second = Sample();
            second.Combat.Units[0].CombatState = new NetworkUnitCombatState
            {
                EngagedUnits = [new NetworkUnitEngagement { UnitId = "d", Rounds = 3 }]
            };

            Assert.That(RealTimeCombatHasher.Compute(second), Is.EqualTo(RealTimeCombatHasher.Compute(first)));
        }

        private static RealTimeCombatSnapshot Sample()
        {
            return new RealTimeCombatSnapshot
            {
                RandomState = 7,
                Combat = new Entities.Combat.NetworkCombatState
                {
                    Units =
                    [
                        new NetworkUnit { Id = "a", Position = new NetworkVector3(1, 2, 3), Descriptor = new NetworkUnitDescriptor { Damage = 4 }, BuffCollection = new NetworkUnitBuffCollection { Buffs = [new NetworkBuff { Id = "b", Rank = 1 }, new NetworkBuff { Id = "c", Rank = 2 }] } },
                        new NetworkUnit { Id = "d", Position = new NetworkVector3(5, 6, 7), Descriptor = new NetworkUnitDescriptor { Damage = 8 } },
                    ],
                    AreaEffects = [new NetworkAreaEffect { Id = "zone-a" }, new NetworkAreaEffect { Id = "zone-b" }],
                },
            };
        }
    }
}
