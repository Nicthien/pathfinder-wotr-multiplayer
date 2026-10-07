using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Mechanics;
using NUnit.Framework;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.HarmonyPatches.Combat;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeAreaReplicaFactoryTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static T Bare<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
        internal static (BlueprintAbilityAreaEffect Blueprint, MechanicsContext Context, NetworkAreaEffect State) Source()
        {
            var blueprint = Bare<BlueprintAbilityAreaEffect>();
            blueprint.AssetGuid = BlueprintGuid.Parse("11111111111111111111111111111111");
            var context = (MechanicsContext)Activator.CreateInstance(typeof(MechanicsContext), Fields,
                null, new object[] { default(JsonConstructorMark) }, null);
            typeof(MechanicsContext).GetField("AssociatedBlueprint", Fields).SetValue(context, blueprint);
            var node = new NetworkBuffContextState
            {
                BlueprintId = blueprint.AssetGuid.ToString(), Params = new(), Direction = new(), SpellLevel = 4,
                Ranks = ((int[])typeof(MechanicsContext).GetField("m_Ranks", Fields).GetValue(context)).ToList(),
                SharedValues = ((int[])typeof(MechanicsContext).GetField("m_SharedValues", Fields).GetValue(context)).ToList(),
                Properties = ((int[])typeof(MechanicsContext).GetField("m_Properties", Fields).GetValue(context)).ToList()
            };
            node.Ranks[0] = 7;
            var state = new NetworkAreaEffect
            {
                Id = "zone", BlueprintId = node.BlueprintId, Position = new(2, 3, 4), TargetPoint = new(5, 6, 7),
                Orientation = 90, TargetOrientation = 45, ContextChain = [node],
                CreationGameTimeTicks = 100, DurationTicks = 200
            };
            return (blueprint, context, state);
        }

        [Test]
        public void CreatesDetachedDataWithoutWorldOrMechanicalComponents()
        {
            var source = Source();
            var area = RealTimeAreaReplicaFactory.CreateDetached(source.Blueprint, source.Context, source.State,
                _ => throw new Exception("no unit required"), _ => Bare<AbilityParams>());
            Assert.That(area.UniqueId, Is.EqualTo("zone"));
            Assert.That(area.Blueprint, Is.SameAs(source.Blueprint));
            Assert.That(area.Context, Is.SameAs(source.Context));
            Assert.That(area.Context.SpellLevel, Is.EqualTo(4));
            Assert.That(((int[])typeof(MechanicsContext).GetField("m_Ranks", Fields).GetValue(area.Context))[0], Is.EqualTo(7));
            Assert.That(area.IsTurnedOn, Is.False);
            Assert.That(area.Facts.List, Is.Empty);
            Assert.That(area.Parts, Is.Not.Null);
            Assert.That(area.HoldingState, Is.Null);
            Assert.That(RealTimeAreaReplicaFactory.IsReplica(area), Is.True);
            Assert.That(RealTimeAreaReplicaFactory.RequiresMechanicalRebuild(new[] { area }), Is.True);
            Assert.Throws<InvalidOperationException>(() => RealTimeAreaReplicaFactory.RequireMechanicalState(new[] { area }));
        }

        [TestCase("blueprint")]
        [TestCase("context")]
        [TestCase("context-shape")]
        [TestCase("caster")]
        [TestCase("id")]
        [TestCase("target")]
        [TestCase("target-orientation")]
        [TestCase("target-point")]
        [TestCase("attached-without-unit")]
        [TestCase("missing-target-unit")]
        [TestCase("ambiguous-target")]
        [TestCase("source-ability")]
        [TestCase("position")]
        [TestCase("orientation")]
        [TestCase("context-cycle")]
        public void InvalidSourceIsRefusedBeforeParameterConversion(string change)
        {
            var source = Source();
            switch (change)
            {
                case "blueprint": source.State.BlueprintId = "another"; break;
                case "context": source.State.ContextChain = null; break;
                case "context-shape": source.State.ContextChain[0].Ranks.Add(1); break;
                case "caster": source.State.CasterUnitId = "unexpected"; break;
                case "id": source.State.Id = " "; break;
                case "target": source.State.TargetPoint = null; break;
                case "target-orientation": source.State.TargetOrientation = float.NaN; break;
                case "target-point": source.State.TargetPoint.X = float.PositiveInfinity; break;
                case "attached-without-unit": source.State.IsAttachedToUnit = true; break;
                case "missing-target-unit": source.State.TargetPoint = null; source.State.TargetUnitId = "missing"; break;
                case "ambiguous-target": source.State.TargetUnitId = "unit"; break;
                case "source-ability": source.State.SourceAbilityBlueprintId = "unknown"; break;
                case "position": source.State.Position = null; break;
                case "orientation": source.State.Orientation = float.NaN; break;
                case "context-cycle": typeof(MechanicsContext).GetProperty("ParentContext", Fields).SetValue(source.Context, source.Context); break;
            }
            var conversions = 0;
            Assert.Throws<InvalidOperationException>(() => RealTimeAreaReplicaFactory.CreateDetached(
                source.Blueprint, source.Context, source.State, _ => null,
                _ => { conversions++; return Bare<AbilityParams>(); }));
            Assert.That(conversions, Is.Zero);
        }

        [Test]
        public void UnitAttachedTargetDoesNotNeedAUnityViewOrPositionGetter()
        {
            var source = Source();
            var unit = Bare<UnitEntityData>();
            typeof(UnitEntityData).GetField("<Descriptor>k__BackingField", Fields).SetValue(unit, Bare<Kingmaker.UnitLogic.UnitDescriptor>());
            var proxy = typeof(Kingmaker.EntitySystem.EntityDataBase).GetField("<Proxy>k__BackingField", Fields);
            proxy.SetValue(unit, FormatterServices.GetUninitializedObject(proxy.FieldType));
            typeof(Kingmaker.EntitySystem.EntityDataBase).GetField("<UniqueId>k__BackingField", Fields).SetValue(unit, "target");
            Assert.That(unit.UniqueId, Is.EqualTo("target"));
            Assert.That(unit == null, Is.False);
            source.State.TargetPoint = null; source.State.TargetUnitId = "target"; source.State.IsAttachedToUnit = true;
            var area = RealTimeAreaReplicaFactory.CreateDetached(source.Blueprint, source.Context, source.State,
                id => id == "target" ? unit : null, _ => Bare<AbilityParams>());
            var target = (Kingmaker.Utility.TargetWrapper)typeof(AreaEffectEntityData).GetField("m_Target", Fields).GetValue(area);
            Assert.That(target.UnitRef.Id, Is.EqualTo("target"));
            Assert.That(((UnityEngine.Vector3)typeof(Kingmaker.Utility.TargetWrapper).GetField("m_Point", Fields).GetValue(target)).x,
                Is.EqualTo(2));
            Assert.That((bool)typeof(AreaEffectEntityData).GetField("m_OnUnit", Fields).GetValue(area), Is.True);
        }

        [Test]
        public void TargetWithoutRegisteredProxyIsRefusedWithoutTemporaryEntityService()
        {
            var source = Source();
            var unit = Bare<UnitEntityData>();
            typeof(UnitEntityData).GetField("<Descriptor>k__BackingField", Fields).SetValue(unit, Bare<Kingmaker.UnitLogic.UnitDescriptor>());
            typeof(Kingmaker.EntitySystem.EntityDataBase).GetField("<UniqueId>k__BackingField", Fields).SetValue(unit, "target");
            source.State.TargetPoint = null; source.State.TargetUnitId = "target";
            Assert.Throws<InvalidOperationException>(() => RealTimeAreaReplicaFactory.CreateDetached(
                source.Blueprint, source.Context, source.State, _ => unit,
                _ => throw new Exception("must not convert parameters")));
            var ordinary = new AreaEffectEntityData(default(JsonConstructorMark));
            source.State.ContextChain = null; source.State.UnitsInside = ["target"];
            Assert.Throws<InvalidOperationException>(() => RealTimeAreaReplication.Apply(ordinary, source.State,
                _ => unit, _ => throw new Exception("must not convert parameters")));
        }

        [Test]
        public void LifecycleGuardsPersistWithoutActiveClient()
        {
            var source = Source();
            var area = RealTimeAreaReplicaFactory.CreateDetached(source.Blueprint, source.Context, source.State,
                _ => null, _ => Bare<AbilityParams>());
            Assert.That(RealTimePresentationRuntime.IsClientActive, Is.False);
            Assert.That(RealTimeAreaReplicaLifecyclePatches.Prepare(), Is.EqualTo(RealTimePresentationRuntime.Enabled));
            Assert.That(RealTimeAreaReplicaLifecyclePatches.Targets().Count(), Is.EqualTo(12));
            Assert.That(RealTimeAreaReplicaLifecyclePatches.BeforeNativeMutation(area), Is.False);
            Assert.That(RealTimeAreaReplicaLifecyclePatches.BeforeNativeMutation(new AreaEffectEntityData(default(JsonConstructorMark))), Is.True);
        }

        [Test]
        public void OrdinaryAreasDoNotRequireMechanicalRebuild()
        {
            var ordinary = new AreaEffectEntityData(default(JsonConstructorMark));
            Assert.That(RealTimeAreaReplicaFactory.RequiresMechanicalRebuild(new[] { ordinary }), Is.False);
            Assert.DoesNotThrow(() => RealTimeAreaReplicaFactory.RequireMechanicalState(new[] { ordinary }));
        }
    }
}
