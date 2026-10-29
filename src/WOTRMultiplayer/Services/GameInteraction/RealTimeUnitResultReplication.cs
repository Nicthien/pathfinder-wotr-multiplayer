using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.UnitLogic;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services.GameInteraction
{
    public static class RealTimeUnitResultReplication
    {
        public static void ApplyPartyCombatEnd(Kingmaker.Player player)
        {
            if (player == null) throw new InvalidOperationException("missing-party-combat-state");
            // The installed setter is a single backing-field write. Do not
            // replay IPartyCombatHandler/quest/death callbacks on the client.
            player.IsInCombat = false;
        }

        public static NetworkUnitVitals CaptureVitals(UnitEntityData unit)
        {
            var state = CaptureValues(unit.Descriptor);
            state.IsInCombat = unit.IsInCombat;
            state.GroupCounterCaptured = unit.Group != null;
            state.GroupCombatCounter = unit.Group?.IsInCombat.m_GuardCount ?? 0;
            return state;
        }

        public static NetworkUnitVitals CaptureValues(UnitDescriptor descriptor) => new()
        {
            Damage = descriptor.Damage, LifeState = (int)descriptor.State.LifeState,
            IsFinallyDead = descriptor.State.IsFinallyDead,
            HitPoints = CaptureStat(descriptor.Stats.HitPoints),
            TemporaryHitPoints = CaptureStat(descriptor.Stats.TemporaryHitPoints),
            NonLethal = CaptureStat(descriptor.Stats.DamageNonLethal)
        };

        internal static NetworkStatValues CaptureStat(ModifiableValue stat) => new()
        {
            BaseValue = stat.BaseValue, ModifiedValue = stat.ModifiedValue,
            ModifiedValueRaw = stat.ModifiedValueRaw, PermanentValue = stat.PermanentValue
        };

        public static NetworkUnitDescriptor Capture(UnitEntityData unit) => new()
        {
            Damage = unit.Descriptor.Damage,
            Vitals = CaptureVitals(unit),
            CalculatedStats = RealTimeCalculatedStatReplication.Capture(unit.Stats.AllStats),
            Stats = new NetworkCharacterStats { DamageNonLethal = unit.Stats.DamageNonLethal.BaseValue },
            State = new NetworkUnitState { IsFinallyDead = unit.State.IsFinallyDead },
            ResourcesCaptured = true,
            Resources = CombatInteractionService.CaptureResources(unit),
            SpellbooksCaptured = true, Spellbooks = RealTimeSpellbookReplication.Capture(unit)
        };

        // Only plain fields/backing-field setters. No UpdateValue, SetLifeState,
        // KillUnit, Spend/Restore, EventBus, XP, loot, quest or death callbacks.
        public static void ApplyValues(UnitDescriptor descriptor, NetworkUnitVitals state)
        {
            if (!RealTimeUnitResultState.IsValid(state)) throw new InvalidOperationException("invalid-unit-vitals");
            if (descriptor?.State == null || descriptor.Stats?.HitPoints == null
                || descriptor.Stats.TemporaryHitPoints == null || descriptor.Stats.DamageNonLethal == null)
                throw new InvalidOperationException("missing-unit-vitals-target");
            descriptor.m_Damage = state.Damage;
            ApplyStat(descriptor.Stats.HitPoints, state.HitPoints);
            ApplyStat(descriptor.Stats.TemporaryHitPoints, state.TemporaryHitPoints);
            ApplyStat(descriptor.Stats.DamageNonLethal, state.NonLethal);
            descriptor.State.LifeState = (UnitLifeState)state.LifeState;
            descriptor.State.IsFinallyDead = state.IsFinallyDead;
        }

        internal static void ApplyStat(ModifiableValue stat, NetworkStatValues state)
        {
            stat.m_BaseValue = state.BaseValue;
            stat.ModifiedValueRaw = state.ModifiedValueRaw;
            stat.ModifiedValue = state.ModifiedValue;
            stat.PermanentValue = state.PermanentValue;
        }

        public static void ApplyVitals(UnitEntityData unit, NetworkUnitVitals state)
        {
            if (unit?.CombatState == null || state.GroupCounterCaptured && unit.Group == null)
                throw new InvalidOperationException("missing-unit-combat-membership");
            // Animation setters can synchronously refresh the descriptor in a few
            // native death/unconsciousness controllers.  Update presentation first,
            // then seal the authoritative plain fields last so the checkpoint sampled
            // in the same main-thread callback cannot observe those side effects.
            var manager = unit.View?.AnimationManager;
            if (manager != null)
            {
                manager.IsDead = state.LifeState == (int)UnitLifeState.Dead;
                manager.IsUnconscious = state.LifeState == (int)UnitLifeState.Unconscious;
            }
            ApplyValues(unit.Descriptor, state);
            unit.CombatState.m_InCombat = state.IsInCombat;
            if (state.GroupCounterCaptured) unit.Group.IsInCombat.m_GuardCount = state.GroupCombatCounter;
        }

        public static void ApplyGroupCombatCounters(
            IEnumerable<(UnitEntityData Unit, NetworkUnitVitals Vitals)> states)
        {
            var expected = new Dictionary<object, (UnitEntityData Unit, int Counter)>();
            foreach (var (unit, vitals) in states)
            {
                if (unit == null || vitals?.GroupCounterCaptured != true) continue;
                if (unit.Group == null) throw new InvalidOperationException("missing-unit-combat-group");
                if (expected.TryGetValue(unit.Group, out var existing)
                    && existing.Counter != vitals.GroupCombatCounter)
                    throw new InvalidOperationException("inconsistent-shared-combat-group-counter");
                expected[unit.Group] = (unit, vitals.GroupCombatCounter);
            }

            // Auras and other buffs can touch this shared guard after a previous
            // member was restored. Seal each group exactly once at the very end.
            foreach (var pair in expected)
                pair.Value.Unit.Group.IsInCombat.m_GuardCount = pair.Value.Counter;
        }

        public static void ApplyValidated(UnitEntityData unit, NetworkUnitDescriptor state)
        {
            RealTimeCalculatedStatReplication.Apply(unit.Stats.AllStats, state.CalculatedStats);
            ApplyVitals(unit, state.Vitals);
            var resources = unit.Descriptor.Resources.PersistantResources.ToDictionary(
                resource => resource.Blueprint.AssetGuid.ToString(), StringComparer.OrdinalIgnoreCase);
            foreach (var resource in state.Resources) resources[resource.BlueprintId].Amount = resource.Amount;
            RealTimeSpellbookReplication.ApplyValidated(unit, state.Spellbooks);
        }
    }
}
