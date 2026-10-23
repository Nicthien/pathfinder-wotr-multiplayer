using System;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using Kingmaker.UnitLogic.Buffs;
using WOTRMultiplayer.Services.GameInteraction;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Contracts;

internal static partial class Program
{
    private sealed class GraphProbeComponent : EntityFactComponent { }
    private sealed class GraphProbePart : EntityPart { }
    private static void GraphField(object owner, string field, object value) => AccessTools.Field(owner.GetType(), field).SetValue(owner, value);
    private static bool ProbeNativeGraphInspection()
    {
        try
        {
            // Detached, synthetic objects only. No native PostLoad, activation,
            // commands, world pools, singleton, rendering or audio are invoked.
            var area = new AreaEffectEntityData(default(JsonConstructorMark));
            GraphField(area, "<UniqueId>k__BackingField", "synthetic-graph-area");
            GraphField(area, "<IsPostLoadExecuted>k__BackingField", true);
            GraphField(area, "Facts", new EntityFactsManager(area));
            GraphField(area, "Parts", new EntityPartsManager(area));
            var fact = (Buff)FormatterServices.GetUninitializedObject(typeof(Buff));
            GraphField(fact, "<UniqueId>k__BackingField", "synthetic-graph-fact");
            GraphField(fact, "<IsPostLoadExecuted>k__BackingField", true);
            GraphField(fact, "Components", new System.Collections.Generic.List<EntityFactComponent>());
            var blueprint = (Kingmaker.UnitLogic.Buffs.Blueprints.BlueprintBuff)FormatterServices.GetUninitializedObject(
                typeof(Kingmaker.UnitLogic.Buffs.Blueprints.BlueprintBuff));
            blueprint.AssetGuid = Kingmaker.Blueprints.BlueprintGuid.Parse("11111111111111111111111111111111");
            GraphField(fact, "<Blueprint>k__BackingField", blueprint);
            GraphField(fact, "<Manager>k__BackingField", area.Facts); area.Facts.List.Add(fact);
            var component = (GraphProbeComponent)FormatterServices.GetUninitializedObject(typeof(GraphProbeComponent));
            GraphField(component, "<Fact>k__BackingField", fact); fact.Components.Add(component);
            var part = (GraphProbePart)FormatterServices.GetUninitializedObject(typeof(GraphProbePart));
            GraphField(part, "<Owner>k__BackingField", area); GraphField(part, "<IsPostLoadExecuted>k__BackingField", true);
            ((System.Collections.Generic.IList<EntityPart>)AccessTools.Field(typeof(EntityPartsManager), "m_Parts").GetValue(area.Parts)).Add(part);
            var inspect = typeof(RealTimeMemorySaver).Assembly.GetType("WOTRMultiplayer.Services.GameInteraction.RealTimeNativeGraphInspection", true)
                .GetMethod("RequireState", BindingFlags.NonPublic | BindingFlags.Static);
            void Inspect() => inspect.Invoke(null, new object[] { Array.Empty<UnitEntityData>(), new[] { area }, (Func<bool>)(() => true) });
            bool Rejects(string reason)
            {
                try { Inspect(); return false; }
                catch (TargetInvocationException error) { return error.InnerException is InvalidOperationException && error.InnerException.Message.Contains(reason); }
            }
            Inspect();
            var safe = !area.IsTurnedOn && !fact.IsTurnedOn && !part.IsTurnedOn;
            GraphField(component, "<Fact>k__BackingField", null);
            safe &= Rejects("component-owner-or-lifecycle");
            GraphField(component, "<Fact>k__BackingField", fact);
            GraphField(part, "<Owner>k__BackingField", null);
            safe &= Rejects("part-owner-or-lifecycle");
            GraphField(part, "<Owner>k__BackingField", area);
            GraphField(area, "<IsDisposed>k__BackingField", true);
            safe &= Rejects("entity-lifecycle");
            GraphField(area, "<IsDisposed>k__BackingField", false); Inspect();
            var capture = inspect.DeclaringType.GetMethod("CaptureState", BindingFlags.NonPublic | BindingFlags.Static);
            NetworkNativeTopology Capture() => (NetworkNativeTopology)capture.Invoke(null,
                new object[] { Array.Empty<UnitEntityData>(), new[] { area }, (Func<bool>)(() => true) });
            var expected = Capture(); fact.Components.Clear();
            safe &= RealTimeNativeTopologyComparison.Difference(expected, Capture())?.Contains("facts-inventory") == true;
            Console.WriteLine("native-graph-inspection: detached-graph=" + safe
                + "; corrupt-component-part-and-entity-rejected=True; missing-component-inventory-rejected=True; native-callbacks-not-invoked=True");
            return safe;
        }
        catch (Exception error) { Console.WriteLine("native-graph-inspection: FAILED: " + error); return false; }
    }
}
