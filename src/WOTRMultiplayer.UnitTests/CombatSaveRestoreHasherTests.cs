using System.Collections.Generic;
using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.UnitTests
{
    public class CombatSaveRestoreHasherTests
    {
        [Test]
        public void NativeLoadNoiseDoesNotChangeRestoreFingerprint()
        {
            var before = State(-66.35f, "AE_old", "FA_aura_old", "FA_spell_old", 4, true);
            var after = State(-66.34f, "AE_new", null, "FA_spell_new", 2, false);
            before.AreaEffects[0].CreationGameTimeTicks = 8107040350000;
            after.AreaEffects[0].CreationGameTimeTicks = 8107334920000;

            Assert.That(RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = before }),
                Is.Not.EqualTo(RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = after })),
                "The live peer fingerprint must remain strict.");
            Assert.That(CombatSaveRestoreHasher.Compute(before), Is.EqualTo(CombatSaveRestoreHasher.Compute(after)));
        }

        [Test]
        public void PersistedMechanicalDifferenceChangesRestoreFingerprint()
        {
            var expected = State(-61.2196541f, "AE_old", "FA_aura_old", "FA_spell_old", 4, true);
            var damaged = State(-61.2196541f, "AE_old", "FA_aura_old", "FA_spell_old", 4, true);
            damaged.Units[0].Descriptor.Damage++;
            var changedBuff = State(-61.2196541f, "AE_old", "FA_aura_old", "FA_spell_old", 4, true);
            changedBuff.Units[0].BuffCollection.Buffs[1].Rank++;
            var changedArea = State(-61.2196541f, "AE_old", "FA_aura_old", "FA_spell_old", 4, true);
            changedArea.AreaEffects[0].BlueprintId = "different-area";

            var fingerprint = CombatSaveRestoreHasher.Compute(expected);
            Assert.That(CombatSaveRestoreHasher.Compute(damaged), Is.Not.EqualTo(fingerprint));
            Assert.That(CombatSaveRestoreHasher.Compute(changedBuff), Is.Not.EqualTo(fingerprint));
            Assert.That(CombatSaveRestoreHasher.Compute(changedArea), Is.Not.EqualTo(fingerprint));
        }

        [Test]
        public void TimedAreaCreationStampIsNotNormalizedByRestoreProjection()
        {
            var expected = State(-61.21f, "AE_old", "FA_aura_old", "FA_spell_old", 4, true);
            var changed = State(-61.21f, "AE_new", null, "FA_spell_new", 2, false);
            expected.AreaEffects[0].DurationTicks = changed.AreaEffects[0].DurationTicks = 60000000;
            expected.AreaEffects[0].CreationGameTimeTicks = 100;
            changed.AreaEffects[0].CreationGameTimeTicks = 200;

            Assert.That(CombatSaveRestoreHasher.Project(expected).AreaEffects[0].CreationGameTimeTicks, Is.EqualTo(100));
            Assert.That(CombatSaveRestoreHasher.Project(changed).AreaEffects[0].CreationGameTimeTicks, Is.EqualTo(200));
        }

        private static NetworkCombatState State(float position, string areaId, string auraId,
            string spellId, int calculatedCasterLevel, bool occupantsCaptured)
        {
            const string unitId = "unit";
            const string auraArea = "aura-area";
            var context = new NetworkBuffContextState
            {
                BlueprintId = "spell-buff", CasterId = unitId, OwnerId = unitId,
                Params = new NetworkAbilityParams { CasterLevel = calculatedCasterLevel },
                Direction = new NetworkVector3()
            };
            var unit = new NetworkUnit
            {
                Id = unitId, BlueprintId = "unit-blueprint", Position = new NetworkVector3(position, 40.108f, -54.07157f),
                Descriptor = new NetworkUnitDescriptor { Damage = 3 },
                BuffCollection = new NetworkUnitBuffCollection
                {
                    Buffs = new List<NetworkBuff>
                    {
                        auraId == null ? null : new NetworkBuff
                        {
                            Id = auraId, BlueprintId = "aura-buff", CasterId = unitId,
                            Rank = 1, IsPermanent = true, IsHidden = true,
                            ContextChain =
                            [
                                new NetworkBuffContextState
                                {
                                    BlueprintId = auraArea, CasterId = unitId, OwnerId = unitId,
                                    Params = new NetworkAbilityParams { CasterLevel = calculatedCasterLevel },
                                    Direction = new NetworkVector3()
                                }
                            ]
                        },
                        new NetworkBuff
                        {
                            Id = spellId, BlueprintId = "spell-buff", CasterId = unitId,
                            Rank = 1, ContextChain = [context]
                        }
                    }.FindAll(buff => buff != null)
                }
            };
            return new NetworkCombatState
            {
                Units = [unit], ProjectilesCaptured = true,
                AreaEffects =
                [
                    new NetworkAreaEffect
                    {
                        Id = areaId, BlueprintId = auraArea, CasterUnitId = unitId, OwnerUnitId = unitId,
                        TargetUnitId = unitId, IsAttachedToUnit = true,
                        Position = new NetworkVector3(position, 40.108f, -54.07157f),
                        ContextChain =
                        [
                            new NetworkBuffContextState
                            {
                                BlueprintId = auraArea, CasterId = unitId, OwnerId = unitId,
                                Params = new NetworkAbilityParams { CasterLevel = calculatedCasterLevel },
                                Direction = new NetworkVector3()
                            }
                        ],
                        UnitsInside = occupantsCaptured ? [unitId] : []
                    }
                ]
            };
        }
    }
}
