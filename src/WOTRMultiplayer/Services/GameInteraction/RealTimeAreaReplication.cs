using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Extensions;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Only host-confirmed data. No Tick, Recalculate, HandleEnd or view callback.
    public static class RealTimeAreaReplication
    {
        private static readonly FieldInfo Creation = AccessTools.Field(typeof(AreaEffectEntityData), "m_CreationTime")
            ?? throw new MissingFieldException(typeof(AreaEffectEntityData).FullName, "m_CreationTime");
        private static readonly FieldInfo Duration = AccessTools.Field(typeof(AreaEffectEntityData), "m_Duration")
            ?? throw new MissingFieldException(typeof(AreaEffectEntityData).FullName, "m_Duration");

        public static string Validate(AreaEffectEntityData area, NetworkAreaEffect state)
        {
            if (area == null || state == null || state.Position == null || state.UnitsInside == null)
                return "invalid-area-state";
            if (!Finite(state.Position.X) || !Finite(state.Position.Y) || !Finite(state.Position.Z)
                || !Finite(state.Orientation)) return "invalid-area-transform";
            if (state.UnitsInside.Any(string.IsNullOrWhiteSpace)
                || state.UnitsInside.Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.UnitsInside.Count)
                return "invalid-area-occupants";
            return RealTimeBuffContextReplication.Validate(area.Context, state.ContextChain);
        }

        public static void Apply(AreaEffectEntityData area, NetworkAreaEffect state,
            Func<string, UnitEntityData> resolveUnit, Func<NetworkAbilityParams, AbilityParams> convertParameters)
        {
            var error = Validate(area, state);
            if (error != null) throw new InvalidOperationException(error);
            // Resolve every occupant before writing any native data.
            var occupants = state.UnitsInside.Select(id =>
            {
                var unit = resolveUnit(id);
                if (unit == null || ReferenceEquals(unit.Proxy, null)
                    || !string.Equals(unit.UniqueId, id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("unresolved-area-occupant:" + id);
                return new AreaEffectEntityData.UnitInfo { Reference = unit, InsideThisTick = true };
            }).ToList();
            RealTimeBuffContextReplication.Apply(area.Context, state.ContextChain, convertParameters);
            area.m_Position = state.Position.ToUnityVector3();
            area.m_Rotation = state.Orientation;
            Creation.SetValue(area, TimeSpan.FromTicks(state.CreationGameTimeTicks));
            Duration.SetValue(area, state.DurationTicks.HasValue ? TimeSpan.FromTicks(state.DurationTicks.Value) : (TimeSpan?)null);
            area.m_UnitsInside = occupants;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
