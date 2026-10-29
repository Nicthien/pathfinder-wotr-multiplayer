using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kingmaker.AI;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Facts;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Commands.Base;
using HarmonyLib;

namespace WOTRMultiplayer.Services.CombatSaving
{
    [Newtonsoft.Json.JsonObject(ItemRequired = Newtonsoft.Json.Required.AllowNull)]
    internal sealed class CombatSaveAiState
    {
        private const int MaximumIneffectiveAttackHistory = 4096;
        private static readonly System.Reflection.FieldInfo CommandResult =
            AccessTools.Field(typeof(UnitCommand), "<Result>k__BackingField");
        private static readonly System.Reflection.FieldInfo CommandFinished =
            AccessTools.Field(typeof(UnitCommand), "<IsFinished>k__BackingField");

        private sealed class CompletedIneffectiveAttackMarker : UnitCommand
        {
            internal CompletedIneffectiveAttackMarker() : base(CommandType.Standard) { }
            public override ResultType OnAction() => ResultType.Success;
        }

        [Newtonsoft.Json.JsonObject(ItemRequired = Newtonsoft.Json.Required.AllowNull)]
        public sealed class ActionState
        {
            public string Slot { get; set; }
            public string Blueprint { get; set; }
            public long? Cooldown { get; set; }
            public int? Count { get; set; }
        }
        public string Unit { get; set; }
        public long StartTime { get; set; }
        public float NextCommandTime { get; set; }
        public int InefficientAttacks { get; set; }
        public string LastTarget { get; set; }
        public string PriorityTarget { get; set; }
        public string[] Unreachable { get; set; }
        public string[] Hard { get; set; }
        public string[] Disliked { get; set; }
        public string[] Bother { get; set; }
        public List<ActionState> Actions { get; set; }
        public int IneffectiveAttackHistoryCount { get; set; }
        public string LastAutoAbility { get; set; }
        public long LastAutoAbilityTime { get; set; }
        public string PriorityTargetFact { get; set; }

        private static Dictionary<string, AiAction> Slots(UnitEntityData unit)
        {
            var result = new Dictionary<string, AiAction>();
            void Add(string name, IEnumerable<AiAction> actions)
            {
                var index = 0;
                foreach (var action in actions) { if (action != null) result[name + index] = action; index++; }
            }
            Add("action:", unit.Brain.Actions); Add("custom:", unit.Brain.CustomActions);
            Add("available:", unit.Brain.AvailableActions);
            var automatic = unit.Brain.m_AutoUseAbility?.DefaultAiAction;
            if (automatic != null) result["automatic"] = automatic;
            return result;
        }
        internal static CombatSaveAiState Capture(UnitEntityData unit)
        {
            var ai = unit.CombatState.AIData; var slots = Slots(unit);
            var ineffectiveAttackHistory = CaptureIneffectiveAttackHistory(ai.ConsequentAttacksWithoutEffect);
            string[] Ids(IEnumerable<UnitEntityData> values) => values.Select(value => value.UniqueId)
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var actions = new List<ActionState>();
            foreach (var action in ai.m_ActionCounts.Keys.Union(ai.m_ActionStartCooldownTime.Keys))
            {
                var slot = slots.FirstOrDefault(pair => ReferenceEquals(pair.Value, action)).Key;
                if (slot == null) throw new InvalidDataException("combat-save-ai-action-unbound");
                actions.Add(new ActionState { Slot = slot, Blueprint = action.Blueprint?.AssetGuid.ToString(),
                    Count = ai.m_ActionCounts.TryGetValue(action, out var count) ? count : (int?)null,
                    Cooldown = ai.m_ActionStartCooldownTime.TryGetValue(action, out var time) ? time.Ticks : (long?)null });
            }
            return new CombatSaveAiState { Unit = unit.UniqueId, StartTime = ai.m_StartCombatTime.Ticks,
                LastAutoAbility = unit.Brain.m_LastAutoUsedAbility?.AssetGuid.ToString(),
                LastAutoAbilityTime = unit.Brain.m_LastAutoUseAbilityTime.Ticks,
                PriorityTargetFact = unit.Brain.PriorityTargetFact?.AssetGuid.ToString(),
                NextCommandTime = ai.NextCommandTime, InefficientAttacks = ai.ConsequentInefficientAttacksCount,
                LastTarget = ai.LastOffensiveStandardActionTarget?.UniqueId, PriorityTarget = ai.ImpatiencePriorityTarget?.UniqueId,
                Unreachable = Ids(ai.UnreachableUnits), Hard = Ids(ai.HardTargets), Disliked = Ids(ai.DislikedTargets), Bother = Ids(ai.BotherUnits),
                Actions = actions.OrderBy(action => action.Slot, StringComparer.Ordinal).ToList(),
                IneffectiveAttackHistoryCount = ineffectiveAttackHistory };
        }
        internal bool IsValid() => !string.IsNullOrEmpty(Unit) && !float.IsNaN(NextCommandTime) && !float.IsInfinity(NextCommandTime)
            && Unreachable != null && Hard != null && Disliked != null && Bother != null
            && InefficientAttacks >= 0 && IneffectiveAttackHistoryCount >= 0
            && IneffectiveAttackHistoryCount <= MaximumIneffectiveAttackHistory
            && Actions != null && Actions.All(action => action != null && !string.IsNullOrEmpty(action.Slot)
                && (action.Count.HasValue || action.Cooldown.HasValue) && (!action.Count.HasValue || action.Count >= 0))
            && Actions.Select(action => action.Slot).Distinct().Count() == Actions.Count
            && References().All(id => !string.IsNullOrEmpty(id));
        internal IEnumerable<string> References() => Unreachable.Concat(Hard).Concat(Disliked).Concat(Bother)
            .Concat(new[] { LastTarget, PriorityTarget }.Where(id => id != null));

        internal void Restore(IReadOnlyDictionary<string, UnitEntityData> units)
        {
            if (!IsValid() || !units.ContainsKey(Unit) || References().Any(id => !units.ContainsKey(id)))
                throw new InvalidDataException("combat-save-ai-reference-invalid");
            var unit = units[Unit]; var ai = unit.CombatState.AIData; var slots = Slots(unit);
            var lastAbility = LastAutoAbility == null ? null : ResourcesLibrary.TryGetBlueprint<BlueprintAbility>(LastAutoAbility);
            var priorityFact = PriorityTargetFact == null ? null : ResourcesLibrary.TryGetBlueprint<BlueprintUnitFact>(PriorityTargetFact);
            if (LastAutoAbility != null && lastAbility == null || PriorityTargetFact != null && priorityFact == null)
                throw new InvalidDataException("combat-save-ai-blueprint-missing");
            foreach (var action in Actions)
                if (!slots.TryGetValue(action.Slot, out var loaded) || loaded.Blueprint?.AssetGuid.ToString() != action.Blueprint)
                    throw new InvalidDataException("combat-save-ai-action-mismatch");
            ai.m_ActionCounts.Clear(); ai.m_ActionStartCooldownTime.Clear();
            foreach (var action in Actions)
            {
                if (action.Count.HasValue) ai.m_ActionCounts.Add(slots[action.Slot], action.Count.Value);
                if (action.Cooldown.HasValue) ai.m_ActionStartCooldownTime.Add(slots[action.Slot], TimeSpan.FromTicks(action.Cooldown.Value));
            }
            void Fill(HashSet<UnitEntityData> target, string[] source) { target.Clear(); target.UnionWith(source.Select(id => units[id])); }
            Fill(ai.UnreachableUnits, Unreachable); Fill(ai.HardTargets, Hard); Fill(ai.DislikedTargets, Disliked); Fill(ai.BotherUnits, Bother);
            ai.m_StartCombatTime = TimeSpan.FromTicks(StartTime); ai.NextCommandTime = NextCommandTime;
            ai.ConsequentInefficientAttacksCount = InefficientAttacks;
            ai.LastOffensiveStandardActionTarget = LastTarget == null ? null : units[LastTarget];
            ai.ImpatiencePriorityTarget = PriorityTarget == null ? null : units[PriorityTarget];
            ai.ConsequentAttacksWithoutEffect.Clear();
            for (var index = 0; index < IneffectiveAttackHistoryCount; index++)
                ai.ConsequentAttacksWithoutEffect.Add(CreateCompletedIneffectiveAttackMarker(unit));
            unit.Brain.m_LastAutoUsedAbility = lastAbility;
            unit.Brain.m_LastAutoUseAbilityTime = TimeSpan.FromTicks(LastAutoAbilityTime);
            unit.Brain.PriorityTargetFact = priorityFact;
        }

        internal static int CaptureIneffectiveAttackHistory(IEnumerable<UnitCommand> commands)
        {
            if (commands == null) throw new InvalidDataException("combat-save-ai-history-missing");
            var count = 0;
            foreach (var command in commands)
            {
                if (command == null || command.Result == UnitCommand.ResultType.None || !command.IsFinished
                    || command.Result == UnitCommand.ResultType.Success && command is UnitAttack attack
                        && attack.LaunchedProjectiles.Any(projectile => projectile != null && projectile.OnTheWay)
                    || command.Result == UnitCommand.ResultType.Success && command is UnitUseAbility ability
                        && (ability.ExecutionProcess == null || !ability.ExecutionProcess.IsEnded))
                    throw new InvalidDataException("combat-save-ai-history-pending");
                if (command.Result == UnitCommand.ResultType.Success) count++;
            }
            if (count > MaximumIneffectiveAttackHistory)
                throw new InvalidDataException("combat-save-ai-history-too-large");
            return count;
        }

        internal static UnitCommand CreateCompletedIneffectiveAttackMarker(UnitEntityData unit)
        {
            if (CommandResult == null || CommandFinished == null)
                throw new InvalidOperationException("combat-save-ai-marker-contract-missing");
            var marker = new CompletedIneffectiveAttackMarker();
            marker.Init(unit);
            CommandResult.SetValue(marker, UnitCommand.ResultType.Success);
            CommandFinished.SetValue(marker, true);
            return marker;
        }
    }
}
