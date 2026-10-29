using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Parts;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Read-only local integrity checks. NOT a host/client topology certificate:
    // a locally consistent but missing component still requires host comparison.
    internal static class RealTimeNativeGraphInspection
    {
        internal const int MaximumNodes = 262144;
        private static readonly FieldInfo Parts = AccessTools.Field(typeof(EntityPartsManager), "m_Parts")
            ?? throw new MissingFieldException(typeof(EntityPartsManager).FullName, "m_Parts");
        private static readonly FieldInfo CommandsOwner = AccessTools.Field(typeof(UnitCommands), "m_Owner")
            ?? throw new MissingFieldException(typeof(UnitCommands).FullName, "m_Owner");
        // UI inspection creates this cache lazily when a unit's buffs are viewed.
        // Its presence therefore depends on which peer opened an inspect tooltip,
        // not on authoritative combat mechanics, and must not invalidate a native
        // combat checkpoint.
        private static readonly HashSet<string> NonMechanicalPartTypes = new(StringComparer.Ordinal)
        {
            typeof(UnitPartInspectedBuffs).FullName
        };

        internal static bool ShouldCapturePart(EntityPart part)
        {
            if (part == null) return true;
            if (NonMechanicalPartTypes.Contains(part.GetType().FullName)) return false;
            // DamageGrace.OnTurnOff removes its last weapon entry but leaves the
            // empty UnitPart attached. A native load correctly omits that inert
            // shell because no active fact calls Ensure<UnitPartDamageGrace>.
            // A populated part is mechanical and must still match exactly.
            return part is not UnitPartDamageGrace damageGrace
                || damageGrace.Weapons == null
                || damageGrace.Weapons.Count != 0;
        }

        internal static NetworkNativeTopology CaptureState(IEnumerable<UnitEntityData> units,
            IEnumerable<AreaEffectEntityData> areas, Func<bool> authorized)
        {
            if (units == null || areas == null) throw new InvalidOperationException("native-graph-pools-missing");
            var unitArray = units.ToArray(); var areaArray = areas.ToArray();
            RequireState(unitArray, areaArray, authorized);
            var nativeUnits = unitArray.ToDictionary(unit => unit.UniqueId, StringComparer.Ordinal);
            foreach (var unit in unitArray)
            {
                // Raw is a fixed array of command slots. Empty slots are normal,
                // unlike a null entry in the pending command queue.
                foreach (var command in unit.Commands.Raw.Where(command => command != null).Concat(unit.Commands.Queue))
                {
                    if (command == null || !ReferenceEquals(command.Executor, unit))
                        throw Invalid(unit.UniqueId, "command-executor-reference");
                    var target = command is UnitAttack attack ? attack.Target
                        : command is UnitUseAbility ability ? ability.Target?.Unit : null;
                    if (target != null && (!nativeUnits.TryGetValue(target.UniqueId, out var nativeTarget)
                        || !ReferenceEquals(nativeTarget, target)))
                        throw Invalid(unit.UniqueId, "command-target-reference");
                }
            }
            var result = new NetworkNativeTopology();
            void Capture(EntityDataBase entity, string kind)
            {
                if (!authorized()) throw new InvalidOperationException("native-graph-authority-revoked");
                var entry = new NetworkNativeEntityTopology { Id = entity.UniqueId, Kind = kind };
                foreach (var part in (IList<EntityPart>)Parts.GetValue(entity.Parts))
                {
                    var partType = part.GetType().FullName;
                    if (ShouldCapturePart(part)) entry.Parts.Add(partType);
                }
                foreach (var fact in entity.Facts.List)
                {
                    if (fact.Blueprint == null) throw Invalid(entity.UniqueId, "fact-blueprint-missing");
                    var item = new NetworkNativeFactTopology
                    { Id = fact.UniqueId, RuntimeType = fact.GetType().FullName, BlueprintId = fact.Blueprint.AssetGuid.ToString() };
                    foreach (var component in fact.Components)
                        item.Components.Add(new NetworkNativeComponentTopology
                        { RuntimeType = component.GetType().FullName, SourceName = component.SourceBlueprintComponentName ?? "" });
                    entry.Facts.Add(item);
                }
                result.Entities.Add(entry);
            }
            foreach (var unit in unitArray) Capture(unit, "unit");
            foreach (var area in areaArray) Capture(area, "area");
            if (!authorized()) throw new InvalidOperationException("native-graph-authority-revoked");
            RealTimeNativeTopologyComparison.RequireValid(result);
            return result;
        }

        internal static void RequireState(IEnumerable<UnitEntityData> units, IEnumerable<AreaEffectEntityData> areas,
            Func<bool> authorized)
        {
            if (authorized == null) throw new ArgumentNullException(nameof(authorized));
            if (units == null || areas == null) throw new InvalidOperationException("native-graph-pools-missing");
            var inspection = new Inspection(authorized);
            foreach (var unit in units)
            {
                inspection.Entity(unit);
                if (unit.Descriptor is null || !ReferenceEquals(unit.Descriptor.Unit, unit)
                    || unit.Descriptor.Stats is null || !ReferenceEquals(unit.Descriptor.Stats.Owner, unit.Descriptor))
                    throw Invalid(unit.UniqueId, "descriptor-or-stats-owner");
                if (unit.CombatState is null || !ReferenceEquals(unit.CombatState.Unit, unit)
                    || unit.CombatState.Cooldown is null || unit.CombatState.TBM is null)
                    throw Invalid(unit.UniqueId, "combat-controller-owner-or-state");
                if (unit.Commands is null || !ReferenceEquals(CommandsOwner.GetValue(unit.Commands), unit)
                    || unit.Commands.Raw == null || unit.Commands.Queue == null)
                    throw Invalid(unit.UniqueId, "commands-controller-owner-or-state");
            }
            foreach (var area in areas) inspection.Entity(area);
            inspection.RequireAuthorized();
        }

        private static InvalidOperationException Invalid(string id, string reason)
            => new("native-graph-invalid:" + (id == null ? "<null>" : id.Substring(0, Math.Min(96, id.Length))) + ":" + reason);

        private sealed class Inspection
        {
            private readonly Func<bool> _authorized;
            private readonly HashSet<object> _nodes = new(ReferenceComparer.Instance);
            private readonly HashSet<string> _entities = new(StringComparer.Ordinal);
            internal Inspection(Func<bool> authorized) => _authorized = authorized;
            internal void RequireAuthorized()
            {
                if (!_authorized()) throw new InvalidOperationException("native-graph-authority-revoked");
            }
            private void Node(object node, string id, string kind)
            {
                RequireAuthorized();
                if (node == null || !_nodes.Add(node)) throw Invalid(id, "missing-or-shared-" + kind);
                if (_nodes.Count > MaximumNodes) throw Invalid(id, "node-limit");
            }
            internal void Entity(EntityDataBase entity)
            {
                Node(entity, entity?.UniqueId, "entity");
                var id = entity.UniqueId;
                if (string.IsNullOrWhiteSpace(id) || !_entities.Add(id)) throw Invalid(id, "entity-id");
                if (entity.IsDisposed || entity.IsDisposingNow || !entity.IsPostLoadExecuted)
                    throw Invalid(id, "entity-lifecycle");
                if (entity.Facts == null || !ReferenceEquals(entity.Facts.Owner, entity)
                    || entity.Facts.List == null || entity.Parts == null || !ReferenceEquals(entity.Parts.Owner, entity)
                    || Parts.GetValue(entity.Parts) is not IList<EntityPart> parts)
                    throw Invalid(id, "manager-owner-or-storage");
                var factIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var fact in entity.Facts.List)
                {
                    Node(fact, id, "fact");
                    if (fact.IsDisposed || !fact.IsPostLoadExecuted || !ReferenceEquals(fact.Manager, entity.Facts)
                        || string.IsNullOrWhiteSpace(fact.UniqueId) || !factIds.Add(fact.UniqueId) || fact.Components == null)
                        throw Invalid(id, "fact-owner-lifecycle-or-id");
                    foreach (var component in fact.Components)
                    {
                        Node(component, id, "component");
                        if (component.IsDisposed || !ReferenceEquals(component.Fact, fact))
                            throw Invalid(id, "component-owner-or-lifecycle");
                    }
                }
                foreach (var part in parts)
                {
                    Node(part, id, "part");
                    if (part.IsDisposed || !part.IsPostLoadExecuted || !ReferenceEquals(part.Owner, entity))
                        throw Invalid(id, "part-owner-or-lifecycle");
                }
                RequireAuthorized();
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new();
            public new bool Equals(object left, object right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
