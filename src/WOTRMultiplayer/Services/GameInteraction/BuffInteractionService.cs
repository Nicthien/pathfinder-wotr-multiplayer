using System;
using System.Collections.Generic;
using System.Linq;
using AutoMapper;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Facts;
using Kingmaker.Blueprints.Root;
using Kingmaker.Designers;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Buffs;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.Utility;
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
        private static readonly System.Reflection.MethodInfo EntityFactSetup = AccessTools.Method(
            typeof(EntityFact), "Setup", new[] { typeof(BlueprintFact) })
            ?? throw new MissingMethodException(typeof(EntityFact).FullName, "Setup");
        private static readonly System.Reflection.FieldInfo BuffContextField = AccessTools.Field(typeof(Buff), "m_Context")
            ?? throw new MissingFieldException(typeof(Buff).FullName, "m_Context");

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
                Buffs = syncableBuffs.Select(buff => RealTimeBuffReplicaStore.ProjectConfirmed(buff,
                    _mapper.Map<NetworkBuff>(buff,
                        x => x.Items[GameProfile.BuffBaseTimeItem] = BuffBaseTime))).ToList(),
                NegativeLevels = _mapper.Map<List<NetworkUnitNegativeLevelsData>>(unit.Get<UnitPartNegativeLevels>()?.m_LevelsData ?? [], x => x.Items[GameProfile.BuffBaseTimeItem] = BuffBaseTime)
            };
            return collection;
        }

        public bool CanRecreateBuff(NetworkBuff buff) => buff != null
            && !string.IsNullOrEmpty(buff.BlueprintId)
            && _recreatableBuffs.ContainsKey(buff.BlueprintId);

        public Buff RestoreNativeBuffForCheckpoint(UnitEntityData unit, NetworkBuff confirmed)
        {
            if (!RealTimePresentationRuntime.Enabled || !RealTimePresentationRuntime.IsClientActive
                || unit == null || confirmed == null || string.IsNullOrWhiteSpace(confirmed.Id)
                || string.IsNullOrWhiteSpace(confirmed.BlueprintId) || confirmed.ContextChain == null
                || GetSyncableUnitBuffs(unit).Any(buff => string.Equals(buff.UniqueId, confirmed.Id,
                    StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("invalid-native-buff-checkpoint-source");
            var blueprint = ResourcesLibrary.TryGetBlueprint<BlueprintBuff>(confirmed.BlueprintId)
                ?? throw new InvalidOperationException("native-buff-blueprint-unavailable:" + confirmed.BlueprintId);
            var context = CreateRealTimeBuffContext(unit, confirmed);
            var duration = confirmed.IsPermanent ? TimeSpan.MaxValue : confirmed.TimeLeft;
            // AddBuff(BlueprintBuff, MechanicsContext, ...) clones the supplied
            // context for the buff and makes it the parent.  The checkpoint
            // already contains the complete chain starting at the buff context,
            // so using AddBuff would insert one extra node. Register the native
            // Buff directly through the owning facts manager, as the game's
            // collection processor still performs the normal attach checks.
            var prepared = PrepareNativeBuffForCheckpoint(blueprint, context, duration, confirmed.Id);
            var restored = unit.Facts.Add(prepared)
                ?? throw new InvalidOperationException("native-buff-restoration-returned-null:" + confirmed.BlueprintId);
            restored.UniqueId = confirmed.Id;
            RealTimeBuffContextReplication.Apply(restored.Context, confirmed.ContextChain,
                parameters => _mapper.Map<AbilityParams>(parameters));
            RealTimeBuffReplication.ApplyValues(restored, confirmed, BuffBaseTime);
            var contextError = RealTimeBuffContextReplication.Validate(restored.Context, confirmed.ContextChain);
            if (contextError != null || RealTimeBuffReplicaStore.IsReplica(restored)
                || !ReferenceEquals(restored.Manager, unit.Facts)
                || !string.Equals(restored.Blueprint?.AssetGuid.ToString(), confirmed.BlueprintId,
                    StringComparison.OrdinalIgnoreCase))
            {
                try { restored.Remove(); } catch { }
                throw new InvalidOperationException("native-buff-restoration-invalid:" + contextError);
            }
            unit.Buffs.UpdateNextEvent();
            _logger.LogInformation("Restored host native buff omitted by same-area load. UnitId={UnitId}, Id={Id}, BlueprintId={BlueprintId}, Components={Components}",
                unit.UniqueId, restored.UniqueId, confirmed.BlueprintId, restored.Components?.Count ?? 0);
            return restored;
        }

        internal static Buff PrepareNativeBuffForCheckpoint(
            BlueprintBuff blueprint,
            MechanicsContext context,
            TimeSpan duration,
            string uniqueId)
        {
            if (blueprint == null || string.IsNullOrWhiteSpace(uniqueId))
                throw new InvalidOperationException("invalid-native-buff-checkpoint-construction");
            if (context != null) return new Buff(blueprint, context, duration) { UniqueId = uniqueId };

            // A few native saves contain buffs whose serialized m_Context is null. The ordinary Buff
            // constructor dereferences that context while checking its parent chain, although the JSON
            // loader legitimately rebuilds the same fact through its serialization constructor. Mirror
            // that native load path, but still run EntityFact.Setup so runtime components remain fully
            // mechanical (this is not a presentation replica).
            var contextless = new Buff(default(JsonConstructorMark));
            EntityFactSetup.Invoke(contextless, new object[] { blueprint });
            contextless.IsPostLoadExecuted = true;
            contextless.UniqueId = uniqueId;
            return contextless;
        }

        public void StageMissingUnitBuffsPresentation(UnitEntityData unit, NetworkUnitBuffCollection unitBuffCollection,
            RealTimeBuffRestoreTransaction transaction)
        {
            if (!RealTimePresentationRuntime.Enabled) throw new InvalidOperationException("presentation-prototype-disabled");
            var expectedIds = RealTimeBuffState.ExpectedIds(unitBuffCollection);
            var current = GetSyncableUnitBuffs(unit);
            var nativeExtras = current.Where(buff => !RealTimeBuffReplicaStore.IsReplica(buff)
                && !expectedIds.Contains(buff.UniqueId)).ToList();
            // The client's buff scheduler is intentionally frozen during host-authoritative
            // real-time combat. Consequently a native buff that the host removed (expiry,
            // death cleanup, aura exit...) cannot disappear locally on its own. Retire the
            // exact fact through the game's normal cleanup path before staging inert replicas;
            // never remove by blueprint because stacked effects are independent instances.
            foreach (var extra in nativeExtras)
            {
                var id = extra.UniqueId;
                extra.Remove();
                if (GetSyncableUnitBuffs(unit).Any(buff =>
                    string.Equals(buff.UniqueId, id, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("native-extra-buff-not-retired:" + id);
                _logger.LogInformation("Retired host-absent native client buff. UnitId={UnitId}, Id={Id}, Name={Name}",
                    unit.UniqueId, id, extra.NameForAcronym);
            }
            current = GetSyncableUnitBuffs(unit);
            var removable = current.Where(buff => RealTimeBuffReplicaStore.IsReplica(buff)
                && !expectedIds.Contains(buff.UniqueId)).ToList();
            var removableIds = new HashSet<string>(removable.Select(buff => buff.UniqueId), StringComparer.OrdinalIgnoreCase);
            var local = GetUnitBuffs(unit);
            var missing = RealTimeBuffState.MissingForPresentationRestore(unitBuffCollection, new NetworkUnitBuffCollection
            {
                Buffs = local.Buffs.Where(buff => !removableIds.Contains(buff.Id)).ToList(), NegativeLevels = local.NegativeLevels
            });
            if (missing.Count == 0 && removable.Count == 0) return;
            var prepared = new List<Buff>();
            var baseTime = BuffBaseTime;
            foreach (var confirmed in missing)
            {
                var blueprint = ResourcesLibrary.TryGetBlueprint<BlueprintBuff>(confirmed.BlueprintId);
                var context = CreateRealTimeBuffContext(unit, confirmed);
                prepared.Add(RealTimeBuffReplicaStore.CreateDetached(blueprint, context, confirmed, baseTime));
            }
            // Resolve the entire unit's missing effects before registering any.
            foreach (var replica in removable)
                transaction.Remove(unit.Facts, replica, () => unit.Buffs.UpdateNextEvent());
            foreach (var replica in prepared)
                transaction.Register(unit.Facts, replica, () => unit.Buffs.UpdateNextEvent());
            _logger.LogInformation("Staged host buff replicas: added={Added}, removed={Removed}. UnitId={UnitId}",
                prepared.Count, removable.Count, unit.UniqueId);
        }

        private MechanicsContext CreateRealTimeBuffContext(UnitEntityData unit, NetworkBuff confirmed)
        {
            if (confirmed.ContextChain?.Count == 0)
                return CreateLegacyNativeBuffContext(unit, confirmed);
            var referencedUnits = confirmed.ContextChain.SelectMany(node => new[] { node.CasterId, node.OwnerId })
                .Where(id => id != null).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => _gameStateLookupService.GetUnitEntity(id)).Where(value => value != null)
                .Concat(new[] { unit }).Distinct().ToList();
            return RealTimeBuffContextFactory.Create(confirmed.ContextChain,
                id => ResourcesLibrary.TryGetBlueprint<BlueprintScriptableObject>(id),
                id => _gameStateLookupService.GetUnitEntity(id),
                id => referencedUnits.SelectMany(owner => owner.Inventory.Items)
                    .FirstOrDefault(item => string.Equals(item.UniqueId, id, StringComparison.OrdinalIgnoreCase)),
                node =>
                {
                    var caster = _gameStateLookupService.GetUnitEntity(node.CasterId);
                    return caster == null ? null : _gameStateLookupService.FindAbility(caster, node.SourceAbility);
                },
                parameters => _mapper.Map<AbilityParams>(parameters));
        }

        private MechanicsContext CreateLegacyNativeBuffContext(UnitEntityData unit, NetworkBuff confirmed)
        {
            var blueprint = ResourcesLibrary.TryGetBlueprint<BlueprintBuff>(confirmed.BlueprintId)
                ?? throw new InvalidOperationException("native-buff-blueprint-unavailable:" + confirmed.BlueprintId);
            UnitEntityData caster = null;
            if (!string.IsNullOrWhiteSpace(confirmed.CasterId))
                caster = _gameStateLookupService.GetUnitEntity(confirmed.CasterId)
                    ?? throw new InvalidOperationException("unresolved-legacy-buff-caster:" + confirmed.CasterId);

            AbilityExecutionContext parent = null;
            if (confirmed.SourceAbility != null)
            {
                var sourceCasterId = confirmed.SourceAbilityCasterId ?? confirmed.CasterId;
                var sourceCaster = string.IsNullOrWhiteSpace(sourceCasterId) ? caster ?? unit
                    : _gameStateLookupService.GetUnitEntity(sourceCasterId)
                      ?? throw new InvalidOperationException("unresolved-legacy-buff-ability-caster:" + sourceCasterId);
                var ability = _gameStateLookupService.FindAbility(sourceCaster, confirmed.SourceAbility)
                    ?? throw new InvalidOperationException("unresolved-legacy-buff-ability");
                var parameters = _mapper.Map<AbilityParams>(confirmed.SourceAbilityParams) ?? AbilityParams.Empty;
                parent = new AbilityExecutionContext(ability, parameters,
                    new TargetWrapper(unit.Position, null, unit), caster: sourceCaster);
            }

            var context = new MechanicsContext(caster, unit.Descriptor, blueprint, parent,
                new TargetWrapper(unit.Position, null, unit));
            if (string.IsNullOrWhiteSpace(confirmed.CasterId))
                AccessTools.Field(typeof(MechanicsContext), "m_CasterRef")
                    .SetValue(context, default(EntityRef<UnitEntityData>));
            return context;
        }

        public string ValidateUnitBuffsPresentation(UnitEntityData unit, NetworkUnitBuffCollection unitBuffCollection)
        {
            var error = RealTimeBuffState.ValidateCollection(unitBuffCollection, GetUnitBuffs(unit));
            if (error != null) return error;
            var existing = GetSyncableUnitBuffs(unit).ToDictionary(buff => buff.UniqueId, StringComparer.OrdinalIgnoreCase);
            foreach (var confirmed in unitBuffCollection.Buffs)
            {
                error = RealTimeBuffContextReplication.Validate(existing[confirmed.Id].Context, confirmed.ContextChain);
                if (error != null) return error + ":" + confirmed.Id;
            }
            return null;
        }

        public void NormalizeNativeBuffTargetsForCheckpoint(UnitEntityData unit,
            NetworkUnitBuffCollection unitBuffCollection,
            RealTimeBuffRestoreTransaction transaction)
        {
            if (!RealTimePresentationRuntime.IsClientActive || unit == null
                || unitBuffCollection?.Buffs == null || transaction == null)
                throw new InvalidOperationException("invalid-native-buff-target-normalization");
            var existing = GetSyncableUnitBuffs(unit).ToDictionary(
                buff => buff.UniqueId, StringComparer.OrdinalIgnoreCase);
            foreach (var confirmed in unitBuffCollection.Buffs)
            {
                if (confirmed?.ContextChain == null || confirmed.ContextChain.Count == 0) continue;
                if (!existing.TryGetValue(confirmed.Id, out var buff))
                    throw new InvalidOperationException("native-buff-target-normalization-missing:" + confirmed.Id);
                string mismatch;
                try
                {
                    RealTimeBuffContextReplication.ApplyConfirmedTargets(buff.Context, confirmed.ContextChain,
                        id => _gameStateLookupService.GetUnitEntity(id));
                    mismatch = RealTimeBuffContextReplication.Validate(buff.Context, confirmed.ContextChain);
                }
                catch (InvalidOperationException failure) when (failure.Message == "buff-context-parent-mismatch")
                { mismatch = failure.Message; }
                if (mismatch == null) continue;

                var previous = buff.Context;
                var replacement = CreateRealTimeBuffContext(unit, confirmed);
                var replacementError = RealTimeBuffContextReplication.Validate(replacement, confirmed.ContextChain);
                if (replacementError != null)
                    throw new InvalidOperationException("native-buff-context-rebuild-invalid:" + replacementError);
                BuffContextField.SetValue(buff, replacement);
                transaction.TrackNativeState(
                    () =>
                    {
                        if (!ReferenceEquals(buff.Context, replacement))
                            throw new InvalidOperationException("native-buff-context-changed-before-commit:" + confirmed.Id);
                    },
                    () =>
                    {
                        if (ReferenceEquals(buff.Context, replacement))
                            BuffContextField.SetValue(buff, previous);
                    });
                _logger.LogInformation(
                    "Rebuilt native buff context chain omitted or reshaped by same-area load. UnitId={UnitId}, BuffId={BuffId}, Depth={Depth}, Reason={Reason}",
                    unit.UniqueId, confirmed.Id, confirmed.ContextChain.Count, mismatch);
            }
        }

        public void UpdateUnitBuffsPresentation(UnitEntityData unit, NetworkUnitBuffCollection unitBuffCollection)
        {
            var buffBaseTime = BuffBaseTime;
            var error = ValidateUnitBuffsPresentation(unit, unitBuffCollection);
            if (error != null) throw new InvalidOperationException($"unit:{unit.UniqueId}:{error}");
            var existing = GetSyncableUnitBuffs(unit).ToDictionary(buff => buff.UniqueId, StringComparer.OrdinalIgnoreCase);
            foreach (var confirmed in unitBuffCollection.Buffs)
            {
                RealTimeBuffContextReplication.Apply(existing[confirmed.Id].Context, confirmed.ContextChain,
                    parameters => _mapper.Map<AbilityParams>(parameters));
                RealTimeBuffReplication.ApplyValues(existing[confirmed.Id], confirmed, buffBaseTime);
                if (RealTimeBuffReplicaStore.IsReplica(existing[confirmed.Id]))
                    RealTimeBuffReplicaStore.Confirm(existing[confirmed.Id], confirmed);
            }
            UpdateNegativeLevels(unit, unitBuffCollection.NegativeLevels, presentationOnly: true);
            unit.Buffs.UpdateNextEvent();
        }

        public void UpdateUnitBuffs(UnitEntityData unit, NetworkUnitBuffCollection unitBuffCollection)
            => UpdateUnitBuffs(unit, unitBuffCollection, removeHiddenExtras: false);

        public void ReconcileUnitBuffsForModeTransition(UnitEntityData unit,
            NetworkUnitBuffCollection unitBuffCollection)
            => UpdateUnitBuffs(unit, unitBuffCollection, removeHiddenExtras: true);

        private void UpdateUnitBuffs(UnitEntityData unit, NetworkUnitBuffCollection unitBuffCollection,
            bool removeHiddenExtras)
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

            RemoveUnitBuffs(unit, localUnitBuffs, removeHiddenExtras);

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

        private void RemoveUnitBuffs(UnitEntityData unit, List<Buff> unitBuffs,
            bool removeHiddenExtras = false)
        {
            var removedBuffs = new List<string>();
            foreach (var buffToRemove in unitBuffs)
            {
                if (buffToRemove.Hidden && !removeHiddenExtras)
                {
                    continue;
                }

                removedBuffs.Add(buffToRemove.NameForAcronym);
                // The authoritative mode-transition checkpoint must also retire
                // client-only hidden aura facts. Remove the exact fact so a
                // same-blueprint stack that is present in the host state is not
                // accidentally discarded by GameHelper.RemoveBuff.
                if (buffToRemove.Hidden)
                    buffToRemove.Remove();
                else
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
