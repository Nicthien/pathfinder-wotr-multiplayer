using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Kingmaker.Blueprints;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using Kingmaker.UnitLogic.Mechanics;
using NUnit.Framework;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeBuffContextFactoryTests
    {
        private static NetworkBuffContextState State()
        {
            var native = (MechanicsContext)Activator.CreateInstance(typeof(MechanicsContext),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { default(Kingmaker.EntitySystem.Persistence.JsonUtility.JsonConstructorMark) }, null);
            List<int> Values(string field) => new((int[])typeof(MechanicsContext)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(native));
            return new NetworkBuffContextState
            {
                BlueprintId = "11111111111111111111111111111111", Params = new(),
                Ranks = Values("m_Ranks"), SharedValues = Values("m_SharedValues"), Properties = Values("m_Properties"),
                Direction = new(1, 2, 3), MainTarget = new(new(4, 5, 6), 0.5f, null)
            };
        }

        private static BlueprintBuff Blueprint()
        {
            var result = (BlueprintBuff)FormatterServices.GetUninitializedObject(typeof(BlueprintBuff));
            result.AssetGuid = BlueprintGuid.Parse("11111111111111111111111111111111");
            return result;
        }

        [Test]
        public void CreatesDetachedParentChainFromConfirmedValuesWithoutWorld()
        {
            var child = State(); var parent = State(); child.Ranks[0] = 42; parent.Ranks[0] = 19;
            var chain = new[] { child, parent }; var parameters = (AbilityParams)FormatterServices.GetUninitializedObject(typeof(AbilityParams));
            var root = RealTimeBuffContextFactory.Create(chain, _ => Blueprint(),
                _ => throw new Exception("unexpected-unit-lookup"), _ => throw new Exception("unexpected-item-lookup"),
                _ => throw new Exception("unexpected-ability-lookup"), _ => parameters);
            Assert.That(RealTimeBuffContextReplication.Validate(root, chain), Is.Null);
            Assert.That(root.ParentContext, Is.Not.Null);
            Assert.That(root.ParentContext.ParentContext, Is.Null);
            Assert.That(root.Params, Is.SameAs(parameters));
            child.Ranks[0] = 99;
            var ranks = (int[])typeof(MechanicsContext).GetField("m_Ranks", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(root);
            Assert.That(ranks[0], Is.EqualTo(42));
        }

        [TestCase("caster")]
        [TestCase("owner")]
        [TestCase("item")]
        [TestCase("target")]
        [TestCase("blueprint")]
        [TestCase("ability")]
        public void RefusesUnresolvedReferencesBeforeParameterApplication(string missing)
        {
            var state = State(); var conversions = 0;
            switch (missing)
            {
                case "caster": state.CasterId = "missing"; break;
                case "owner": state.OwnerId = "missing"; break;
                case "item": state.SourceItemId = "missing"; break;
                case "target": state.MainTarget.UnitId = "missing"; break;
                case "ability": state.IsAbilityContext = true; state.SourceAbility = new() { BlueprintId = state.BlueprintId }; break;
            }
            Assert.Throws<InvalidOperationException>(() => RealTimeBuffContextFactory.Create(new[] { state },
                _ => missing == "blueprint" ? null : Blueprint(), _ => null, _ => null, _ => null,
                _ => { conversions++; return null; }));
            Assert.That(conversions, Is.Zero);
        }
    }
}
