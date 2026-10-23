using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.Utility;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Extensions;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Detached data only. Registration/view ownership belong to the restore
    // transaction, not to the native mechanical constructor or save loader.
    public static class RealTimeAreaReplicaFactory
    {
        private static readonly ConditionalWeakTable<AreaEffectEntityData, object> Replicas = new();
        private static FieldInfo Field(Type type, string name) => AccessTools.Field(type, name)
            ?? throw new MissingFieldException(type.FullName, name);

        public static bool IsReplica(AreaEffectEntityData area) => area != null && Replicas.TryGetValue(area, out _);
        internal static void MarkNativeInert(AreaEffectEntityData area, object owner) => Replicas.Add(area, owner);
        internal static bool IsNativeInertOwner(AreaEffectEntityData area, object owner) =>
            Replicas.TryGetValue(area, out var current) && ReferenceEquals(current, owner);
        internal static void ReleaseNativeInert(AreaEffectEntityData area, object owner)
        {
            if (!Replicas.TryGetValue(area, out var current) || !ReferenceEquals(current, owner))
                throw new InvalidOperationException("native-area-retirement-marker-ownership-changed");
            Replicas.Remove(area);
        }
        public static bool RequiresMechanicalRebuild(IEnumerable<AreaEffectEntityData> areas) =>
            // Pool enumeration hides suppressed/out-of-game replicas. They
            // still exist in the save graph and must block mechanical reuse.
            areas is EntityPool<AreaEffectEntityData> pool ? pool.All.Any(IsReplica)
                : areas != null && areas.Any(IsReplica);

        public static void RequireMechanicalState(IEnumerable<AreaEffectEntityData> areas)
        {
            if (RequiresMechanicalRebuild(areas))
                throw new InvalidOperationException("presentation-areas-require-mechanical-reconstruction");
        }

        public static AreaEffectEntityData CreateDetached(BlueprintAbilityAreaEffect blueprint, MechanicsContext context,
            NetworkAreaEffect state, Func<string, UnitEntityData> resolveUnit,
            Func<NetworkAbilityParams, AbilityParams> convertParameters)
        {
            if (ReferenceEquals(blueprint, null) || context == null || state == null
                || string.IsNullOrWhiteSpace(state.Id) || !Same(blueprint.AssetGuid.ToString(), state.BlueprintId)
                || state.Position == null || !Finite(state.Position.X) || !Finite(state.Position.Y) || !Finite(state.Position.Z)
                || !Finite(state.Orientation)
                || state.ContextChain == null || state.ContextChain.Count == 0
                || RealTimeBuffContextChain.Validate(state.ContextChain) != null
                || !Same(state.ContextChain[0].BlueprintId, state.BlueprintId)
                || !Same(context.m_CasterRef.Id, state.CasterUnitId)
                || !Same(context.m_OwnerRef.Id, state.OwnerUnitId)
                // Validate topology before the recursive SourceAbility getter.
                || RealTimeBuffContextReplication.Validate(context, state.ContextChain) != null
                || !Same(context.SourceAbility?.AssetGuid.ToString(), state.SourceAbilityBlueprintId))
                throw new InvalidOperationException("invalid-area-replica-source");
            if (state.TargetOrientation.HasValue && !Finite(state.TargetOrientation.Value)
                || state.TargetPoint != null && (!Finite(state.TargetPoint.X) || !Finite(state.TargetPoint.Y) || !Finite(state.TargetPoint.Z))
                || state.TargetUnitId == null && state.TargetPoint == null
                || state.TargetUnitId != null && state.TargetPoint != null
                || state.IsAttachedToUnit && state.TargetUnitId == null)
                throw new InvalidOperationException("invalid-area-replica-target");

            TargetWrapper target;
            if (state.TargetUnitId != null)
            {
                var unit = resolveUnit(state.TargetUnitId);
                if (unit == null || ReferenceEquals(unit.Proxy, null) || !Same(unit.UniqueId, state.TargetUnitId))
                    throw new InvalidOperationException("unresolved-area-replica-target");
                // A missing proxy makes EntityRef's native constructor enter
                // TemporaryEntityService. Only already-registered units qualify.
                // Do not read unit.Position, which depends on its Unity view.
                target = new TargetWrapper(state.Position.ToUnityVector3(), state.TargetOrientation, unit);
            }
            else target = new TargetWrapper(state.TargetPoint.ToUnityVector3(), state.TargetOrientation);

            // The JSON constructor initializes only inert defaults. Ordinary
            // constructors Recalculate/CheckSelective/HandleSpawn and subscribe
            // damage triggers. None of those paths is used here.
            var area = new AreaEffectEntityData(default(JsonConstructorMark));
            // Native JSON defaults mark an entity in-game before registration.
            // A detached replica must remain outside the enumerator until its
            // explicit pool membership has been staged by the transaction.
            Field(typeof(EntityDataBase), "m_IsInGame").SetValue(area, false);
            Field(typeof(AreaEffectEntityData), "m_Blueprint").SetValue(area, blueprint);
            Field(typeof(AreaEffectEntityData), "m_Context").SetValue(area, context);
            Field(typeof(AreaEffectEntityData), "m_Target").SetValue(area, target);
            Field(typeof(AreaEffectEntityData), "m_OnUnit").SetValue(area, state.IsAttachedToUnit);
            Field(typeof(EntityDataBase), "<UniqueId>k__BackingField").SetValue(area, state.Id);
            // These constructors only create empty owner-bound lists/caches.
            // A JSON entity otherwise has null managers and cannot attach a view.
            Field(typeof(EntityDataBase), "Parts").SetValue(area, new EntityPartsManager(area));
            Field(typeof(EntityDataBase), "Facts").SetValue(area, new EntityFactsManager(area));
            RealTimeAreaReplication.Apply(area, state, resolveUnit, convertParameters);
            if (area.IsTurnedOn || area.Facts.List.Any())
                throw new InvalidOperationException("area-replica-created-mechanical-state");
            Replicas.Add(area, new object());
            return area;
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
