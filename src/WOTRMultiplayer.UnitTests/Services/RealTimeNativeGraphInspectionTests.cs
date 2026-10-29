using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using HarmonyLib;
using Kingmaker.Controllers.Combat;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Buffs;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Parts;
using NUnit.Framework;
using WOTRMultiplayer.Services.GameInteraction;
using WOTRMultiplayer.Networking;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeNativeGraphInspectionTests
    {
        private sealed class TestPart : EntityPart { }
        private sealed class TestComponent : EntityFactComponent { }
        private static T Bare<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
        private static void Field(object owner, string name, object value) => AccessTools.Field(owner.GetType(), name).SetValue(owner, value);

        private static void Initialize(EntityDataBase entity, string id)
        {
            Field(entity, "<UniqueId>k__BackingField", id);
            Field(entity, "<IsPostLoadExecuted>k__BackingField", true);
            Field(entity, "Facts", new EntityFactsManager(entity));
            Field(entity, "Parts", new EntityPartsManager(entity));
        }
        private static AreaEffectEntityData Area(string id = "area")
        {
            var area = Bare<AreaEffectEntityData>(); Initialize(area, id); return area;
        }
        private static UnitEntityData Unit()
        {
            var unit = Bare<UnitEntityData>(); Initialize(unit, "unit");
            var descriptor = Bare<UnitDescriptor>();
            Field(unit, "<Descriptor>k__BackingField", descriptor);
            Field(descriptor, "<Unit>k__BackingField", unit);
            var stats = Bare<CharacterStats>(); Field(descriptor, "Stats", stats);
            Field(stats, "<Owner>k__BackingField", descriptor);
            var combat = Bare<UnitCombatState>();
            Field(combat, "Unit", unit); Field(combat, "Cooldown", new UnitCombatState.Cooldowns());
            Field(combat, "TBM", new UnitCombatState.TBMState(unit));
            Field(unit, "<CombatState>k__BackingField", combat);
            Field(unit, "<Commands>k__BackingField", new UnitCommands(unit, false));
            return unit;
        }
        private static Buff Fact(EntityDataBase owner, string id = "fact")
        {
            var fact = Bare<Buff>();
            Field(fact, "<UniqueId>k__BackingField", id);
            Field(fact, "<Manager>k__BackingField", owner.Facts);
            Field(fact, "<IsPostLoadExecuted>k__BackingField", true);
            Field(fact, "Components", new List<EntityFactComponent>());
            var blueprint = Bare<Kingmaker.UnitLogic.Buffs.Blueprints.BlueprintBuff>();
            blueprint.AssetGuid = Kingmaker.Blueprints.BlueprintGuid.Parse("11111111111111111111111111111111");
            Field(fact, "<Blueprint>k__BackingField", blueprint);
            owner.Facts.List.Add(fact);
            return fact;
        }
        private static EntityFactComponent Component(Buff fact)
        {
            var component = Bare<TestComponent>();
            Field(component, "<Fact>k__BackingField", fact); fact.Components.Add(component);
            return component;
        }
        private static IList<EntityPart> Parts(EntityDataBase entity)
            => (IList<EntityPart>)AccessTools.Field(typeof(EntityPartsManager), "m_Parts").GetValue(entity.Parts);
        private static EntityPart Part(EntityDataBase entity)
        {
            var part = Bare<TestPart>();
            Field(part, "<Owner>k__BackingField", entity);
            Field(part, "<IsPostLoadExecuted>k__BackingField", true);
            Parts(entity).Add(part); return part;
        }
        private static void Require(UnitEntityData unit, AreaEffectEntityData area)
            => RealTimeNativeGraphInspection.RequireState(new[] { unit }, new[] { area }, () => true);

        [Test]
        public void CapturePreservesNativeOrderAndDetectsACompletelyMissingComponent()
        {
            var unit = Unit(); var area = Area(); var fact = Fact(unit); var component = Component(fact); Part(area);
            var reference = RealTimeNativeGraphInspection.CaptureState(new[] { unit }, new[] { area }, () => true);
            Assert.That(reference.Entities[0].Facts[0].Components[0].RuntimeType, Is.EqualTo(component.GetType().FullName));
            Assert.That(reference.Entities[0].Facts[0].BlueprintId, Is.EqualTo(fact.Blueprint.AssetGuid.ToString()));
            fact.Components.Clear();
            var restored = RealTimeNativeGraphInspection.CaptureState(new[] { unit }, new[] { area }, () => true);
            Assert.That(RealTimeNativeTopologyComparison.Difference(reference, restored), Does.Contain("facts-inventory"));
            Assert.That(unit.IsTurnedOn, Is.False);
        }

        [Test]
        public void ConsistentSyntheticGraphIsInspectedWithoutActivationOrOwnerChanges()
        {
            var unit = Unit(); var area = Area();
            var fact = Fact(unit); var component = Component(fact); var part = Part(area);
            var commands = unit.Commands; var combat = unit.CombatState;
            Require(unit, area); Require(unit, area);
            Assert.That(unit.Commands, Is.SameAs(commands)); Assert.That(unit.CombatState, Is.SameAs(combat));
            Assert.That(component.Fact, Is.SameAs(fact)); Assert.That(part.Owner, Is.SameAs(area));
            Assert.That(unit.IsTurnedOn || area.IsTurnedOn || fact.IsTurnedOn || part.IsTurnedOn, Is.False);
        }

        [Test]
        public void CaptureIgnoresThePeerLocalInspectedBuffsUiCache()
        {
            var unit = Unit(); var area = Area();
            var inspectedBuffs = Bare<UnitPartInspectedBuffs>();
            Field(inspectedBuffs, "<Owner>k__BackingField", unit);
            Field(inspectedBuffs, "<IsPostLoadExecuted>k__BackingField", true);
            Parts(unit).Add(inspectedBuffs);

            var topology = RealTimeNativeGraphInspection.CaptureState(
                new[] { unit }, new[] { area }, () => true);

            Assert.That(topology.Entities.Single(entity => entity.Id == "unit").Parts,
                Does.Not.Contain(typeof(UnitPartInspectedBuffs).FullName));
            Assert.That(Parts(unit), Does.Contain(inspectedBuffs));
        }

        [Test]
        public void CaptureIgnoresOnlyAnEmptyDamageGraceShell()
        {
            var empty = new UnitPartDamageGrace();
            var active = new UnitPartDamageGrace();
            active.Weapons.Add(new UnitPartDamageGrace.DamageGraceEntry());

            Assert.That(RealTimeNativeGraphInspection.ShouldCapturePart(empty), Is.False);
            Assert.That(RealTimeNativeGraphInspection.ShouldCapturePart(active), Is.True);
        }

        [TestCase("descriptor")]
        [TestCase("descriptor-owner")]
        [TestCase("stats")]
        [TestCase("stats-owner")]
        [TestCase("combat")]
        [TestCase("combat-owner")]
        [TestCase("cooldown")]
        [TestCase("tbm")]
        [TestCase("commands")]
        [TestCase("commands-owner")]
        [TestCase("commands-raw")]
        [TestCase("commands-queue")]
        public void CorruptUnitControllerOrOwnerIsRejected(string fault)
        {
            var unit = Unit(); var area = Area();
            switch (fault)
            {
                case "descriptor": Field(unit, "<Descriptor>k__BackingField", null); break;
                case "descriptor-owner": Field(unit.Descriptor, "<Unit>k__BackingField", Unit()); break;
                case "stats": Field(unit.Descriptor, "Stats", null); break;
                case "stats-owner": Field(unit.Descriptor.Stats, "<Owner>k__BackingField", Bare<UnitDescriptor>()); break;
                case "combat": Field(unit, "<CombatState>k__BackingField", null); break;
                case "combat-owner": Field(unit.CombatState, "Unit", Unit()); break;
                case "cooldown": Field(unit.CombatState, "Cooldown", null); break;
                case "tbm": Field(unit.CombatState, "TBM", null); break;
                case "commands": Field(unit, "<Commands>k__BackingField", null); break;
                case "commands-owner": Field(unit.Commands, "m_Owner", Unit()); break;
                case "commands-raw": Field(unit.Commands, "m_Commands", null); break;
                case "commands-queue": Field(unit.Commands, "m_Queue", null); break;
            }
            var error = Assert.Throws<InvalidOperationException>(() => Require(unit, area));
            Assert.That(error.Message, Does.StartWith("native-graph-invalid:unit:"));
        }

        [TestCase("id")]
        [TestCase("disposed")]
        [TestCase("disposing")]
        [TestCase("postload")]
        [TestCase("facts-manager")]
        [TestCase("facts-owner")]
        [TestCase("parts-manager")]
        [TestCase("parts-owner")]
        [TestCase("parts-storage")]
        [TestCase("fact-null")]
        [TestCase("fact-disposed")]
        [TestCase("fact-postload")]
        [TestCase("fact-owner")]
        [TestCase("fact-id")]
        [TestCase("fact-duplicate-id")]
        [TestCase("fact-shared")]
        [TestCase("components-storage")]
        [TestCase("component-null")]
        [TestCase("component-disposed")]
        [TestCase("component-owner")]
        [TestCase("component-shared")]
        [TestCase("part-null")]
        [TestCase("part-disposed")]
        [TestCase("part-postload")]
        [TestCase("part-owner")]
        [TestCase("part-shared")]
        public void CorruptNativeEntityMembersAreRejected(string fault)
        {
            var unit = Unit(); var area = Area();
            var fact = Fact(area); var component = Component(fact); var part = Part(area);
            switch (fault)
            {
                case "id": Field(area, "<UniqueId>k__BackingField", " "); break;
                case "disposed": Field(area, "<IsDisposed>k__BackingField", true); break;
                case "disposing": Field(area, "<IsDisposingNow>k__BackingField", true); break;
                case "postload": Field(area, "<IsPostLoadExecuted>k__BackingField", false); break;
                case "facts-manager": Field(area, "Facts", null); break;
                case "facts-owner": Field(area.Facts, "<Owner>k__BackingField", unit); break;
                case "parts-manager": Field(area, "Parts", null); break;
                case "parts-owner": Field(area.Parts, "<Owner>k__BackingField", unit); break;
                case "parts-storage": Field(area.Parts, "m_Parts", null); break;
                case "fact-null": area.Facts.List.Add(null); break;
                case "fact-disposed": Field(fact, "<IsDisposed>k__BackingField", true); break;
                case "fact-postload": Field(fact, "<IsPostLoadExecuted>k__BackingField", false); break;
                case "fact-owner": Field(fact, "<Manager>k__BackingField", unit.Facts); break;
                case "fact-id": Field(fact, "<UniqueId>k__BackingField", ""); break;
                case "fact-duplicate-id": Fact(area); break;
                case "fact-shared": area.Facts.List.Add(fact); break;
                case "components-storage": Field(fact, "Components", null); break;
                case "component-null": fact.Components.Add(null); break;
                case "component-disposed": Field(component, "<IsDisposed>k__BackingField", true); break;
                case "component-owner": Field(component, "<Fact>k__BackingField", Fact(unit)); break;
                case "component-shared": fact.Components.Add(component); break;
                case "part-null": Parts(area).Add(null); break;
                case "part-disposed": Field(part, "<IsDisposed>k__BackingField", true); break;
                case "part-postload": Field(part, "<IsPostLoadExecuted>k__BackingField", false); break;
                case "part-owner": Field(part, "<Owner>k__BackingField", unit); break;
                case "part-shared": Parts(area).Add(part); break;
            }
            var error = Assert.Throws<InvalidOperationException>(() => Require(unit, area));
            Assert.That(error.Message, Does.StartWith("native-graph-invalid:"));
        }

        [Test]
        public void EntityIdentityCollisionAcrossPoolsIsRejected()
        {
            var error = Assert.Throws<InvalidOperationException>(() => Require(Unit(), Area("unit")));
            Assert.That(error.Message, Does.EndWith(":entity-id"));
        }

        [Test]
        public void AuthorityRevocationInterruptsInspectionWithoutMutatingTheGraph()
        {
            var unit = Unit(); var area = Area(); var fact = Fact(area); Component(fact); Part(area);
            var commands = unit.Commands; var calls = 0;
            var error = Assert.Throws<InvalidOperationException>(() =>
                RealTimeNativeGraphInspection.RequireState(new[] { unit }, new[] { area }, () => ++calls < 5));
            Assert.That(error.Message, Is.EqualTo("native-graph-authority-revoked"));
            Assert.That(unit.Commands, Is.SameAs(commands));
            Assert.That(area.Facts.List.Single(), Is.SameAs(fact));
            Require(unit, area);
        }

        [Test]
        public void NullEntityIsRejectedWithoutAccessToWorld()
        {
            var error = Assert.Throws<InvalidOperationException>(() => Require(null, Area()));
            Assert.That(error.Message, Does.Contain("missing-or-shared-entity"));
        }

        [Test]
        public void NodeLimitStopsAnOversizedGraphWithoutActivatingOrRemovingComponents()
        {
            var unit = Unit(); var area = Area(); var fact = Fact(area);
            for (var index = 0; index < RealTimeNativeGraphInspection.MaximumNodes; index++) Component(fact);
            var error = Assert.Throws<InvalidOperationException>(() => Require(unit, area));
            Assert.That(error.Message, Does.EndWith(":node-limit"));
            Assert.That(fact.Components.Count, Is.EqualTo(RealTimeNativeGraphInspection.MaximumNodes));
            Assert.That(fact.IsTurnedOn, Is.False);
        }
    }
}
