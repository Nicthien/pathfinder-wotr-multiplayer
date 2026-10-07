using System;
using System.Collections.Generic;
using System.Linq;
using AutoMapper;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Root;
using Kingmaker.Designers;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Buffs;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.UnitLogic.Parts;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Abstractions.GameInteraction.CombatLog;
using WOTRMultiplayer.Config.Mapping;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Extensions;

namespace WOTRMultiplayer.Services.GameInteraction
{
    public class BuffInteractionService : IBuffInteractionService
    {
        private readonly ILogger<BuffInteractionService> _logger;
        private readonly IPlayerNotificationService _playerNotificationService;
        private readonly IGameStateLookupService _gameStateLookupService;
        private readonly IMapper _mapper;

        private readonly Dictionary<string, List<ChainedBuff>> _recreatableBuffs = new()
        {
            { "6ffd93355fb3bcf4592a5d976b1d32a9", [] }, // fighting defensively
            { "a0bc0525895932b42bfd47f1544e6e35", // Archon's Aura
                [
                    new ChainedBuff { BlueprintId = "94d278269422cb54a8d456f38004e2f9", IsExclusive = true } // Archon's Aura Immunity
                ]
            },
        };

        private TimeSpan BuffBaseTime => Game.Instance.TimeController.GameTime;

        public BuffInteractionService(
            ILogger<BuffInteractionService> logger,
            IPlayerNotificationService playerNotificationService,
            IGameStateLookupService gameStateLookupService,
            IMapper mapper)
        {
            _logger = logger;
            _playerNotificationService = playerNotificationService;
            _gameStateLookupService = gameStateLookupService;
            _mapper = mapper;
        }

        public NetworkUnitBuffCollection GetUnitBuffs(UnitEntityData unit)
        {
            var syncableBuffs = GetSyncableUnitBuffs(unit);
            var collection = new NetworkUnitBuffCollection
            {
                Buffs = _mapper.Map<List<NetworkBuff>>(syncableBuffs, x => x.Items[GameProfile.BuffBaseTimeItem] = BuffBaseTime),
                NegativeLevels = _mapper.Map<List<NetworkUnitNegativeLevelsData>>(unit.Get<UnitPartNegativeLevels>()?.m_LevelsData ?? [], x => x.Items[GameProfile.BuffBaseTimeItem] = BuffBaseTime)
            };
            return collection;
        }

        public bool CanRecreateBuff(NetworkBuff buff) => buff != null
            && !string.IsNullOrEmpty(buff.BlueprintId)
            && _recreatableBuffs.ContainsKey(buff.BlueprintId);

        public void UpdateUnitBuffsPresentation(UnitEntityData unit, NetworkUnitBuffCollection unitBuffCollection)
        {
            var buffBaseTime = BuffBaseTime;
            var error = RealTimeBuffState.ValidateCollection(unitBuffCollection, GetUnitBuffs(unit));
            if (error != null) throw new InvalidOperationException($"unit:{unit.UniqueId}:{error}");
            var existing = GetSyncableUnitBuffs(unit).ToDictionary(buff => buff.UniqueId, StringComparer.OrdinalIgnoreCase);
            foreach (var confirmed in unitBuffCollection.Buffs)
                RealTimeBuffReplication.ApplyValues(existing[confirmed.Id], confirmed, buffBaseTime);
            UpdateNegativeLevels(unit, unitBuffCollection.NegativeLevels, presentationOnly: true);
            unit.Buffs.UpdateNextEvent();
        }

        public void UpdateUnitBuffs(UnitEntityData unit, NetworkUnitBuffCollection unitBuffCollection)
        {
            var buffBaseTime = BuffBaseTime;
            var localUnitBuffs = GetSyncableUnitBuffs(unit);
            var remoteUnitBuffs = unitBuffCollection.Buffs.ToList();

            for (int i = remoteUnitBuffs.Count - 1; i >= 0; i--)
            {
                var networkBuff = remoteUnitBuffs[i];
                var buff = localUnitBuffs.FirstOrDefault(x => (string.Equals(x.UniqueId, networkBuff.Id, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Blueprint.AssetGuid.ToString(), networkBuff.BlueprintId, StringComparison.OrdinalIgnoreCase)));

                if (buff == null)
                {
                    _logger.LogWarning("Unable to find buff to update. UnitId={UnitId}, Id={Id}, Rank={Rank}, BlueprintId={BlueprintId}, Name={Name}, Duration={Duration}", unit.UniqueId, networkBuff.Id, networkBuff.Rank, networkBuff.BlueprintId, networkBuff.Name, networkBuff.TimeLeft);
                    continue;
                }

                UpdateExistingUnitBuff(unit, buff, buffBaseTime, networkBuff);
                remoteUnitBuffs.Remove(networkBuff);
                localUnitBuffs.Remove(buff);
            }

            RemoveUnitBuffs(unit, localUnitBuffs);

            CreateUnitBuffs(unit, remoteUnitBuffs, buffBaseTime);

            UpdateNegativeLevels(unit, unitBuffCollection.NegativeLevels);

            unit.Buffs.UpdateNextEvent();
            _logger.LogDebug("Unit buffs have been updated. UnitId={UnitId}", unit.UniqueId);
        }

        private void UpdateNegativeLevels(UnitEntityData unit, List<NetworkUnitNegativeLevelsData> negativeLevels, bool presentationOnly = false)
        {
            if (negativeLevels.Count == 0)
            {
                if (presentationOnly) return; // No UnitPart lifecycle on a presentation client.
                unit.Remove<UnitPartNegativeLevels>();
                return;
            }

            var localLevels = unit.Get<UnitPartNegativeLevels>()?.m_LevelsData;
            if (localLevels == null || localLevels.Count != negativeLevels.Count)
            {
                _logger.LogWarning("Negative levels cannot be repaired without matching game effects. UnitId={UnitId}, LocalCount={LocalCount}, HostCount={HostCount}",
                    unit.UniqueId, localLevels?.Count ?? 0, negativeLevels.Count);
                return;
            }

            var hostGroups = negativeLevels.GroupBy(level =>
                (level.Count, level.EnergyDrainType, level.SavingThrowType))
                .ToDictionary(group => group.Key, group => group.OrderBy(level => level.Duration).ToList());
            var localGroups = localLevels.GroupBy(level =>
                (level.Count, level.Type, level.SavingThrowType))
                .ToDictionary(group => group.Key, group => group.OrderBy(level => level.EndTime).ToList());
            if (hostGroups.Count != localGroups.Count || hostGroups.Any(group =>
                    !localGroups.TryGetValue(group.Key, out var matches) || matches.Count != group.Value.Count))
            {
                _logger.LogWarning("Negative level types differ; timer-only repair refused. UnitId={UnitId}", unit.UniqueId);
                return;
            }

            var baseTime = BuffBaseTime;
            foreach (var group in hostGroups)
            {
                var matches = localGroups[group.Key];
                for (var index = 0; index < group.Value.Count; index++)
                {
                    var duration = group.Value[index].Duration;
                    var local = matches[index];
                    if (duration.HasValue != local.EndTime.HasValue
                        || duration.HasValue && (duration.Value <= TimeSpan.Zero
                            || local.RelatedBuff == null || local.RelatedBuff.IsPermanent))
                    {
                        _logger.LogWarning("Negative level timer cannot be repaired without a matching active buff. UnitId={UnitId}, Count={Count}, DrainType={DrainType}",
                            unit.UniqueId, local.Count, local.Type);
                        return;
                    }
                    if (duration.HasValue && group.Value.Count > 1
                        && Math.Abs((decimal)duration.Value.Ticks - (local.EndTime.Value - baseTime).Ticks)
                            > TimeSpan.FromSeconds(1).Ticks)
                    {
                        _logger.LogWarning("Ambiguous negative level timer repair refused. UnitId={UnitId}, Count={Count}, DrainType={DrainType}, Matches={Matches}",
                            unit.UniqueId, local.Count, local.Type, group.Value.Count);
                        return;
                    }
                }
            }

            foreach (var group in hostGroups)
            {
                var matches = localGroups[group.Key];
                for (var index = 0; index < group.Value.Count; index++)
                {
                    var hostDuration = group.Value[index].Duration;
                    var local = matches[index];
                    if (!hostDuration.HasValue)
                        continue;
                    var localDuration = local.EndTime.Value - baseTime;
                    if (Math.Abs((decimal)hostDuration.Value.Ticks - localDuration.Ticks)
                        <= TimeSpan.FromSeconds(1).Ticks)
                        continue;
                    if (presentationOnly) local.RelatedBuff.EndTime = baseTime.SafeAdd(hostDuration.Value);
                    else local.RelatedBuff.SetDuration(hostDuration.Value);
                    local.EndTime = baseTime.SafeAdd(hostDuration.Value);
                    _logger.LogInformation("Temporary negative level timer repaired. UnitId={UnitId}, Count={Count}, DrainType={DrainType}, PreviousDuration={PreviousDuration}, HostDuration={HostDuration}",
                        unit.UniqueId, local.Count, local.Type, localDuration, hostDuration.Value);
                }
            }
        }

        private void CreateUnitBuffs(UnitEntityData unit, List<NetworkBuff> remoteUnitBuffs, TimeSpan buffBaseTime)
        {
            var addedBuffs = new List<string>();
            foreach (var buffToAdd in remoteUnitBuffs)
            {
                // TODO: straightforward buff creation is unstable af with a lot of caveats.
                // Something like the 'Burning' DoT or 'FightingDefensively' works fine, but many buffs either have an underlying `UnitPart` or produce side effects that need to be synced as well.
                if (!_recreatableBuffs.TryGetValue(buffToAdd.BlueprintId, out var chainedBuffs))
                {
                    continue;
                }

                var caster = _gameStateLookupService.GetUnitEntity(buffToAdd.CasterId) ?? unit;
                var buff = ResourcesLibrary.TryGetBlueprint<BlueprintBuff>(buffToAdd.BlueprintId);
                if (buff == null)
                {
                    _logger.LogError("Missing blueprint for a buff. BlueprintId={BlueprintId}", buffToAdd.BlueprintId);
                    continue;
                }

                RemoveExclusiveChainedBuffs(unit, buff.AssetGuid.ToString(), chainedBuffs);

                MechanicsContext parent = GetBuffParentContext(unit, buffToAdd);
                var context = new MechanicsContext(caster, unit, buff, parent);

                var duration = buffToAdd.IsPermanent ? TimeSpan.MaxValue : buffToAdd.TimeLeft;
                var newBuff = unit.Buffs.AddBuff(buff, context, duration);
                newBuff.NextTickTime = buffToAdd.NextTickTime == TimeSpan.MaxValue ? TimeSpan.MaxValue : buffBaseTime.SafeAdd(buffToAdd.NextTickTime);
                if (buffToAdd.Rank > 0 && newBuff.Rank != buffToAdd.Rank)
                {
                    newBuff.SetRank(buffToAdd.Rank);
                }

                _logger.LogInformation("Buff has been added. UnitId={UnitId}, Id={Id}, Rank={Rank}, BlueprintId={BlueprintId}, Name={Name}, IsPermanent={IsPermanent}, PlannedDuration={PlannedDuration}", unit.UniqueId, newBuff.UniqueId, newBuff.Rank, buffToAdd.BlueprintId, newBuff.NameForAcronym, newBuff.IsPermanent, newBuff.PlannedDuration);
            }

            if (addedBuffs.Count > 0)
            {
                _playerNotificationService.AddCombatText(WellKnownKeys.GameNotifications.Combat.Buffs.AddedBuffs.Key, CombatTextSeverity.Critical, new UnitLogParameter(unit.UniqueId), string.Join(", ", remoteUnitBuffs.Select(x => x.Name)));
            }
        }

        private MechanicsContext GetBuffParentContext(UnitEntityData unit, NetworkBuff buffToAdd)
        {
            if (buffToAdd.SourceAbility == null)
            {
                return null;
            }

            var abilityCaster = _gameStateLookupService.GetUnitEntity(buffToAdd.SourceAbilityCasterId);
            if (abilityCaster == null)
            {
                _logger.LogError("Unable to find caster unit for buff context. UnitId={UnitId}", buffToAdd.SourceAbilityCasterId);
                return null;
            }

            var ability = _gameStateLookupService.FindAbility(abilityCaster, buffToAdd.SourceAbility);
            if (ability == null)
            {
                _logger.LogError("Unable to find ability for buff context. UnitId={UnitId}, AbilityBlueprintId={AbilityBlueprintId}, AbilityName={AbilityName}", buffToAdd.SourceAbilityCasterId, buffToAdd.SourceAbility.BlueprintId, buffToAdd.SourceAbility.Name);
                return null;
            }

            var abilityParams = _mapper.Map<AbilityParams>(buffToAdd.SourceAbilityParams);
            var context = new AbilityExecutionContext(ability, abilityParams, new Kingmaker.Utility.TargetWrapper(unit));
            return context;
        }

        private List<Buff> GetSyncableUnitBuffs(UnitEntityData unit)
        {
            // negative levels are handled separately
            var buffs = unit.Buffs.Enumerable.Where(x => x.Blueprint != BlueprintRoot.Instance.SystemMechanics.NegativeLevelsBuff).ToList();
            return buffs;
        }

        private void RemoveUnitBuffs(UnitEntityData unit, List<Buff> unitBuffs)
        {
            var removedBuffs = new List<string>();
            foreach (var buffToRemove in unitBuffs)
            {
                if (buffToRemove.Hidden)
                {
                    continue;
                }

                removedBuffs.Add(buffToRemove.NameForAcronym);
                GameHelper.RemoveBuff(unit, buffToRemove.Blueprint);
                _logger.LogInformation("Buff has been removed. UnitId={UnitId}, Id={Id}, Name={Name}", unit.UniqueId, buffToRemove.UniqueId, buffToRemove.NameForAcronym);

                RemoveExclusiveChainedBuffs(unit, buffToRemove.Blueprint.AssetGuid.ToString());
            }

            if (removedBuffs.Count > 0)
            {
                _playerNotificationService.AddCombatText(WellKnownKeys.GameNotifications.Combat.Buffs.RemovedBuffs.Key, CombatTextSeverity.Debug, new UnitLogParameter(unit.UniqueId), string.Join(", ", removedBuffs));
            }
        }

        private void UpdateExistingUnitBuff(UnitEntityData unit, Buff buff, TimeSpan buffBaseTime, NetworkBuff networkBuff)
        {
            try
            {
                if (!buff.IsPermanent)
                {
                    buff.SetDuration(networkBuff.TimeLeft);
                    if (!buff.Hidden && buff.Rank != networkBuff.Rank)
                    {
                        buff.SetRank(networkBuff.Rank);
                    }
                }

                buff.NextResourceSpendingTime = networkBuff.NextResourceSpendingTime == TimeSpan.MaxValue ? TimeSpan.MaxValue : buffBaseTime.SafeAdd(networkBuff.NextResourceSpendingTime);
                buff.NextTickTime = networkBuff.NextTickTime == TimeSpan.MaxValue ? TimeSpan.MaxValue : buffBaseTime.SafeAdd(networkBuff.NextTickTime);

                _logger.LogDebug("Updated buff. UnitId={UnitId}, Id={Id}, Name={Name}, Duration={Duration}, Rank={Rank}, IsHidden={IsHidden}, NextResourceSpendingTime={NextResourceSpendingTime}, NextTickTime={NextTickTime}, BuffBaseTime={BuffBaseTime} NetworkNextTickTime={NetworkNextTickTime}, NetworkNextPendingTime={NetworkNextPendingTime}",
                    unit.UniqueId, buff.UniqueId, buff.NameForAcronym, networkBuff.TimeLeft, networkBuff.Rank, networkBuff.IsHidden, buff.NextResourceSpendingTime, buff.NextTickTime, buffBaseTime, networkBuff.NextTickTime, networkBuff.NextResourceSpendingTime);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while updating buff. UnitId={UnitId}, Id={Id}, Name={Name}, Duration={Duration}, NextResourceSpendingTime={NextResourceSpendingTime}, NextTickTime={NextTickTime}",
                    unit.UniqueId, buff.UniqueId, buff.NameForAcronym, networkBuff.TimeLeft, buff.NextResourceSpendingTime, buff.NextTickTime);
            }
        }

        private void RemoveExclusiveChainedBuffs(UnitEntityData unit, string parentBlueprintId, List<ChainedBuff> chainedBuffs)
        {
            foreach (var chained in chainedBuffs)
            {
                if (!chained.IsExclusive)
                {
                    return;
                }

                var chainedBuffToRemove = unit.Buffs.Enumerable.FirstOrDefault(x => string.Equals(x.Blueprint.AssetGuid.ToString(), chained.BlueprintId, StringComparison.OrdinalIgnoreCase));
                if (chainedBuffToRemove != null)
                {
                    GameHelper.RemoveBuff(unit, chainedBuffToRemove.Blueprint);
                    _logger.LogInformation("Chained Buff has been removed. UnitId={UnitId}, Id={Id}, Name={Name}", unit.UniqueId, chainedBuffToRemove.UniqueId, chainedBuffToRemove.NameForAcronym);
                }
            }
        }

        private void RemoveExclusiveChainedBuffs(UnitEntityData unit, string parentBlueprintId)
        {
            if (!_recreatableBuffs.TryGetValue(parentBlueprintId, out var chainedBuffs))
            {
                return;
            }

            RemoveExclusiveChainedBuffs(unit, parentBlueprintId, chainedBuffs);
        }

        private class ChainedBuff
        {
            public string BlueprintId { get; set; }

            public bool IsExclusive { get; set; }
        }
    }
}
