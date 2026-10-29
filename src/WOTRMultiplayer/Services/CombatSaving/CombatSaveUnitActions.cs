using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.Services.CombatSaving
{
    // UnitEntityData.CombatState is not part of the native opt-in save contract.
    // These counters are authoritative, unlike ActionsState's cursor predictions.
    internal sealed class CombatSaveUnitActions
    {
        public string Unit { get; set; }
        public float InitiativeCooldown { get; set; }
        public float Standard { get; set; }
        public float Move { get; set; }
        public float Swift { get; set; }
        public float OpportunityCooldown { get; set; }
        public int Initiative { get; set; }
        public int InitiativeRandom { get; set; }
        public int OpportunityCount { get; set; }
        public int ExecutedAttackNumber { get; set; }
        public bool HitThisRound { get; set; }
        public bool HadTurn { get; set; }
        public bool InCombat { get; set; }
        public bool Prepared { get; set; }
        public bool NotSurprised { get; set; }
        [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
        public bool GroupCounterCaptured { get; set; }
        [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
        public int GroupCombatCounter { get; set; }
        [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
        public List<NetworkAbilityResource> Resources { get; set; }
        public long LastMoveTicks { get; set; }
        public long LastDeflectTicks { get; set; }
        public long LastSurpriseTicks { get; set; }
        [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
        public float PreviousPositionX { get; set; }
        [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
        public float PreviousPositionY { get; set; }
        [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
        public float PreviousPositionZ { get; set; }

        internal bool IsValid() => !string.IsNullOrEmpty(Unit)
            && (!GroupCounterCaptured || GroupCombatCounter >= 0)
            && RealTimeResourceState.Validate(Resources, Resources) == null
            && new[] { InitiativeCooldown, Standard, Move, Swift, OpportunityCooldown,
                PreviousPositionX, PreviousPositionY, PreviousPositionZ }
                .All(value => !float.IsNaN(value) && !float.IsInfinity(value));

        internal static CombatSaveUnitActions Capture(UnitEntityData unit)
        {
            var state = unit.CombatState; var cooldown = state.Cooldown;
            return new CombatSaveUnitActions
            {
                Unit = unit.UniqueId, InitiativeCooldown = cooldown.Initiative,
                Standard = cooldown.StandardAction, Move = cooldown.MoveAction, Swift = cooldown.SwiftAction,
                OpportunityCooldown = cooldown.AttackOfOpportunity, Initiative = state.Initiative,
                InitiativeRandom = state.InitiativeRandom, OpportunityCount = state.AttackOfOpportunityCount,
                ExecutedAttackNumber = state.ExecutedAttackNumber, HitThisRound = state.HitThisRound,
                HadTurn = state.HadTurnInTMBCombat, InCombat = state.IsInCombat, Prepared = state.Prepared,
                NotSurprised = state.NotSurprised, LastMoveTicks = state.m_LastUsageOfMoveActionTime.Ticks,
                GroupCounterCaptured = unit.Group != null,
                GroupCombatCounter = unit.Group?.IsInCombat.m_GuardCount ?? 0,
                Resources = CombatInteractionService.CaptureResources(unit),
                LastDeflectTicks = state.m_LastDeflectArrowTime.Ticks, LastSurpriseTicks = state.LastSurpriseActionTime.Ticks,
                PreviousPositionX = unit.PreviousPosition.x, PreviousPositionY = unit.PreviousPosition.y,
                PreviousPositionZ = unit.PreviousPosition.z
            };
        }

        internal void Restore(UnitEntityData unit)
        {
            if (unit?.UniqueId != Unit) throw new InvalidDataException("combat-save-action-unit-mismatch");
            var state = unit.CombatState; var cooldown = state.Cooldown;
            cooldown.Initiative = InitiativeCooldown; cooldown.StandardAction = Standard;
            cooldown.MoveAction = Move; cooldown.SwiftAction = Swift; cooldown.AttackOfOpportunity = OpportunityCooldown;
            state.Initiative = Initiative; state.InitiativeRandom = InitiativeRandom;
            // Direct field assignment avoids the native setter consuming an AoO cooldown.
            state.m_AttackOfOpportunityCount = OpportunityCount;
            state.ExecutedAttackNumber = ExecutedAttackNumber; state.HitThisRound = HitThisRound;
            state.HadTurnInTMBCombat = HadTurn; state.m_InCombat = InCombat; state.Prepared = Prepared;
            state.NotSurprised = NotSurprised;
            if (GroupCounterCaptured)
            {
                if (unit.Group == null) throw new InvalidDataException("combat-save-unit-group-missing");
                unit.Group.IsInCombat.m_GuardCount = GroupCombatCounter;
            }
            var currentResources = CombatInteractionService.CaptureResources(unit);
            if (RealTimeResourceState.Validate(Resources, currentResources) != null)
                throw new InvalidDataException("combat-save-unit-resources-mismatch");
            var resources = unit.Descriptor.Resources.PersistantResources.ToDictionary(
                resource => resource.Blueprint.AssetGuid.ToString(), StringComparer.OrdinalIgnoreCase);
            foreach (var resource in Resources) resources[resource.BlueprintId].Amount = resource.Amount;
            state.m_LastUsageOfMoveActionTime = TimeSpan.FromTicks(LastMoveTicks);
            state.m_LastDeflectArrowTime = TimeSpan.FromTicks(LastDeflectTicks);
            state.LastSurpriseActionTime = TimeSpan.FromTicks(LastSurpriseTicks);
            // Native HasMotionThisTick compares Position with this frame-local value.
            // RequireReady rejected any real motion before capture, but the native
            // save rounds Position while this supplement retains full precision.
            // Rebase on the position the loader actually produced, otherwise the
            // harmless rounding difference is mistaken for a pending movement.
            RebaseLoadedMotionBaseline(unit);
        }

        internal void RebaseLoadedMotionBaseline(UnitEntityData unit)
        {
            if (unit?.UniqueId != Unit) throw new InvalidDataException("combat-save-action-unit-mismatch");
            unit.PreviousPosition = LoadedMotionBaseline(unit.Position);
        }

        internal void RestoreSavedIdlePosition(UnitEntityData unit)
        {
            if (unit?.UniqueId != Unit) throw new InvalidDataException("combat-save-action-unit-mismatch");
            var saved = SavedIdlePosition();
            if (unit.CombatState != null)
                unit.CombatState.PreventAttacksOfOpporunityNextFrame = true;
            if (unit.View != null)
                unit.View.transform.position = saved;
            unit.Position = saved;
            unit.PreviousPosition = saved;
        }

        internal UnityEngine.Vector3 SavedIdlePosition()
            => new(PreviousPositionX, PreviousPositionY, PreviousPositionZ);

        internal static UnityEngine.Vector3 LoadedMotionBaseline(UnityEngine.Vector3 loadedPosition)
            => loadedPosition;
    }
}
