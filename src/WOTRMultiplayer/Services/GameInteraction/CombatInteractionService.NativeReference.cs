using System;
using System.Linq;
using System.Threading.Tasks;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Classes;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence;
using Kingmaker.Enums;
using Kingmaker.GameModes;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Buffs;
using Kingmaker.UnitLogic.FactLogic;
using Kingmaker.UnitLogic.Parts;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking;
using NetworkNativeTopology = WOTRMultiplayer.Networking.Messages.Contracts.NetworkNativeTopology;

namespace WOTRMultiplayer.Services.GameInteraction
{
    public partial class CombatInteractionService
    {
        // Commit is deliberately limited to a drained, fully native world.
        // Presentation replicas and unrelated repair obligations never qualify.
        public Task<bool> TryCommitNativeReferenceStateAsync(object expectedWorld, object repairOwner,
            NetworkCombatState reference, Func<bool> canCommit)
        {
            if (canCommit == null) throw new ArgumentNullException(nameof(canCommit));
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var attempts = 0;
            string lastTransientReason = null;

            void RetryAtNextStableTick(string reason)
            {
                if (!string.Equals(lastTransientReason, reason, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "Native Commit is waiting for the post-load world to drain. Reason={Reason}, Attempt={Attempt}",
                        reason, attempts);
                    lastTransientReason = reason;
                }
                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException("native-commit-safe-point-timeout:" + reason);
                _ = Task.Delay(50).ContinueWith(_ =>
                {
                    if (!completion.Task.IsCompleted) _mainThreadAccessor.Post(AttemptCommit);
                }, TaskScheduler.Default);
            }

            void AttemptCommit()
            {
                try
                {
                    if (completion.Task.IsCompleted) return;
                    attempts++;
                    var game = Game.Instance;
                    if (!canCommit()) { completion.TrySetResult(false); return; }
                    if (!RealTimePresentationRuntime.IsClientActive)
                        throw new InvalidOperationException("native-commit-client-runtime-inactive");
                    if (!ReferenceEquals(game?.State, expectedWorld))
                        throw new InvalidOperationException("native-commit-world-replaced");
                    if (repairOwner is not RealTimeNativeLoadLease lease)
                        throw new InvalidOperationException("native-commit-receipt-owner-invalid");
                    if (!RealTimeNativeGraphRepair.IsOnlyObligation(expectedWorld, repairOwner))
                        throw new InvalidOperationException("native-commit-repair-obligation-invalid");
                    if (reference?.NativeTopology == null)
                        throw new InvalidOperationException("native-commit-reference-topology-missing");

                    // Restoring native facts and spell slots can enqueue one last
                    // Owlcat entity-creation pass after reference verification.
                    // Commit must wait for that queue (and the surrounding load,
                    // pause and save machinery) to become observably idle instead
                    // of treating this normal one-frame race as a failed repair.
                    string transientReason = null;
                    if (!game.IsPaused) transientReason = "pause-not-confirmed";
                    else if (RealTimeNativeLoadLease.IsActive) transientReason = "native-load-lease-active";
                    else if (!lease.IsDrainedAndCompleted) transientReason = "native-load-receipt-not-drained";
                    else if (game.CurrentMode != GameModeType.Default && game.CurrentMode != GameModeType.Pause)
                        transientReason = "game-mode-" + game.CurrentMode;
                    else if (IsInCrusadeTacticalCombat()) transientReason = "crusade-combat-active";
                    else if (LoadingProcess.Instance.IsLoadingInProcess) transientReason = "loading-process-active";
                    else if (game.EntityCreator.CreationQueue.Any()) transientReason = "entity-creation-pending";
                    else if (game.SaveManager.CurrentState != SaveManager.State.None)
                        transientReason = "save-manager-" + game.SaveManager.CurrentState;
                    else if (game.SaveManager.CommitInProgress) transientReason = "save-commit-in-progress";
                    if (transientReason != null)
                    {
                        RetryAtNextStableTick(transientReason);
                        return;
                    }
                    RealTimeNativeCharacterUiGuard.RequireIdle(game);
                    RealTimeBuffReplicaStore.RequireMechanicalState(game.State.Units.All);
                    RealTimeAreaReplicaFactory.RequireMechanicalState(game.State.AreaEffects);
                    using (var projection = new CombatSaveDigestScope())
                    {
                        NormalizePostLoadOnlyEncumbranceParts(reference.NativeTopology,
                            game.State.Units.All, "commit");
                        var topology = RealTimeNativeGraphInspection.CaptureState(game.State.Units.All, game.State.AreaEffects, canCommit);
                        var difference = RealTimeNativeTopologyComparison.Difference(reference.NativeTopology, topology);
                        var actual = GetRealTimeCombatState();
                        var temporalDifference = RealTimeCombatTemporalDiff.Summarize(reference, actual);
                        var referenceHash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = reference });
                        var actualHash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = actual });
                        if (difference != null)
                            throw new InvalidOperationException("native-commit-reference-changed:" + difference);
                        if (temporalDifference != null || referenceHash != actualHash)
                        {
                            // Owlcat can run one delayed post-load recalculation
                            // between reference verification and Commit (a dead
                            // unit's HP/encumbrance refresh is one observed case).
                            // Reassert only the already verified authoritative
                            // checkpoint at this final drained tick, then require
                            // the exact graph and digest again before releasing the
                            // repair obligation.
                            using var finalRestoration = new RealTimeBuffRestoreTransaction();
                            ApplyRealTimeCombatState(reference, finalRestoration, requireNativeEntities: true);
                            NormalizePostLoadOnlyEncumbranceParts(reference.NativeTopology,
                                game.State.Units.All, "final-commit");
                            topology = RealTimeNativeGraphInspection.CaptureState(
                                game.State.Units.All, game.State.AreaEffects, canCommit);
                            difference = RealTimeNativeTopologyComparison.Difference(reference.NativeTopology, topology);
                            actual = GetRealTimeCombatState();
                            temporalDifference = RealTimeCombatTemporalDiff.Summarize(reference, actual);
                            actualHash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = actual });
                            if (difference != null || temporalDifference != null || referenceHash != actualHash)
                                throw new InvalidOperationException("native-commit-reference-changed:"
                                    + (difference ?? temporalDifference ?? "canonical-hash-mismatch"));
                            finalRestoration.VerifyAndCommit(reference, actual);
                        }
                    }
                    if (!canCommit()) { completion.TrySetResult(false); return; }
                    RealTimeNativeGraphRepair.Remove(expectedWorld, repairOwner);
                    completion.TrySetResult(true); // Still paused; network Resume is separate.
                }
                catch (Exception error) { completion.TrySetException(error); }
            }

            _mainThreadAccessor.Post(AttemptCommit);
            return completion.Task;
        }

        public NetworkCombatState GetRealTimeNativeReferenceState(bool retainHistoricalCombatUnits = false)
        {
            var game = Game.Instance;
            if (!RealTimePresentationRuntime.Enabled || RealTimePresentationRuntime.IsClientActive
                || game?.State == null || game.Player == null || !CombatSaving.CombatSavePauseLease.IsWorldPaused(game) || RealTimeNativeLoadLease.IsActive
                || game.SaveManager.CurrentState != SaveManager.State.None || game.SaveManager.CommitInProgress
                || LoadingProcess.Instance.IsLoadingInProcess || game.EntityCreator.CreationQueue.Any()
                || game.CurrentMode != GameModeType.Default && game.CurrentMode != GameModeType.Pause
                || IsInCrusadeTacticalCombat() || game.Player?.StartPreset?.Campaign?.DlcReward != null)
                throw new InvalidOperationException("native-reference-host-not-at-safe-point");
            RealTimeNativeCharacterUiGuard.RequireIdle(game);
            var world = game.State;
            using (var projection = new CombatSaveDigestScope())
            {
                var state = GetRealTimeCombatState();
                // The RTwP participant registry deliberately remembers defeated,
                // withdrawn and otherwise historical encounter units. Owlcat's
                // native save does not serialize that presentation registry; it
                // restores only the units whose native CombatState is currently
                // in combat. A restore digest must therefore describe that native
                // membership instead of stale RT presentation history.
                RetainNativeCombatUnits(state, world.Units.InCombat().Select(unit => unit.UniqueId),
                    retainHistoricalCombatUnits);
                state.NativeTopology = RealTimeNativeGraphInspection.CaptureState(world.Units.All, world.AreaEffects,
                    () => ReferenceEquals(Game.Instance, game) && ReferenceEquals(game.State, world) && CombatSaving.CombatSavePauseLease.IsWorldPaused(game));
                state.NativeUnits = world.Units.All.Select(unit => new Entities.Units.NetworkUnit
                {
                    Id = unit.UniqueId,
                    BuffCollection = _buffInteractionService.GetUnitBuffs(unit)
                }).ToList();
                return state;
            }
        }

        internal static void RetainNativeCombatUnits(NetworkCombatState state,
            System.Collections.Generic.IEnumerable<string> nativeUnitIds,
            bool retainHistoricalCombatUnits = false)
        {
            if (state?.Units == null || nativeUnitIds == null)
                throw new InvalidOperationException("native-combat-membership-missing");
            // At RTwP combat end Owlcat has already cleared native InCombat,
            // while the immutable final checkpoint deliberately retains every
            // encounter participant. The native reconstruction must certify
            // that same final checkpoint instead of collapsing it to zero units.
            if (retainHistoricalCombatUnits) return;
            var native = new System.Collections.Generic.HashSet<string>(nativeUnitIds,
                StringComparer.OrdinalIgnoreCase);
            state.Units = state.Units.Where(unit => unit != null && native.Contains(unit.Id)).ToList();
        }
        // Restores confirmed data on native objects, never missing presentation
        // entities. Does NOT certify every mechanical component or lift the
        // world's repair obligation; the enclosing Commit must do that later.
        public Task<NetworkCombatState> TryRestoreNativeReferenceStateAsync(object expectedWorld, object repairOwner,
            NetworkCombatState reference, Func<bool> canApply)
        {
            if (canApply == null) throw new ArgumentNullException(nameof(canApply));
            var completion = new TaskCompletionSource<NetworkCombatState>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainThreadAccessor.Post(() =>
            {
                try
                {
                    if (!canApply()) { completion.TrySetResult(null); return; }
                    // Owlcat's native save does not preserve every
                    // AbilityExecutionContext.Ability reference on long-lived
                    // buffs. The source blueprint remains native and stable;
                    // use the same projection as combat-save verification for
                    // this complete native repair transaction only.
                    using var projection = new CombatSaveDigestScope();
                    if (!RealTimePresentationRuntime.Enabled || reference?.Units == null || reference.AreaEffects == null
                        || reference.NativeTopology == null
                        || repairOwner is not RealTimeNativeLoadLease { IsDrainedAndCompleted: true }
                        || !RealTimeNativeGraphRepair.Contains(expectedWorld, repairOwner))
                        throw new InvalidOperationException("native-reference-receipt-or-state-invalid");
                    reference = _mapper.Map<NetworkCombatState>(ProtoBuf.Serializer.DeepClone(
                        _mapper.Map<Networking.Messages.Contracts.NetworkCombatState>(reference)));
                    var game = Game.Instance;
                    void RequireCurrentWorld()
                    {
                        if (!RealTimePresentationRuntime.IsClientActive || !canApply()
                            || !ReferenceEquals(game?.State, expectedWorld) || !ReferenceEquals(Game.Instance, game)
                            || !game.IsPaused || RealTimeNativeLoadLease.IsActive
                            || game.CurrentMode != GameModeType.Default && game.CurrentMode != GameModeType.Pause
                            || IsInCrusadeTacticalCombat()
                            || game.SaveManager.CurrentState != SaveManager.State.None || game.SaveManager.CommitInProgress
                            || repairOwner is not RealTimeNativeLoadLease { IsDrainedAndCompleted: true }
                            || LoadingProcess.Instance.IsLoadingInProcess || game.EntityCreator.CreationQueue.Any()
                            || !RealTimeNativeGraphRepair.Contains(expectedWorld, repairOwner))
                            throw new InvalidOperationException("native-reference-world-not-ready");
                        RealTimeNativeCharacterUiGuard.RequireIdle(game);
                        RealTimeBuffReplicaStore.RequireMechanicalState(game.State.Units.All);
                        RealTimeAreaReplicaFactory.RequireMechanicalState(game.State.AreaEffects);
                    }
                    RequireCurrentWorld();
                    NetworkNativeTopology RequireExpectedTopology()
                    {
                        var actualTopology = RealTimeNativeGraphInspection.CaptureState(game.State.Units.All, game.State.AreaEffects, canApply);
                        var topologyDifference = RealTimeNativeTopologyComparison.Difference(reference.NativeTopology, actualTopology);
                        if (topologyDifference != null) throw new InvalidOperationException("native-reference-topology-differs:" + topologyDifference);
                        return actualTopology;
                    }
                    NetworkCombatState applied;
                    using (var transaction = new RealTimeBuffRestoreTransaction())
                    {
                        try
                        {
                        // A same-area native load can leave inert area effects from
                        // the retired client world in the new entity pools. Remove
                        // only empty/safely-retirable effects absent from the host's
                        // native manifest; every other extra entity remains fatal.
                        var expectedEntities = new System.Collections.Generic.HashSet<string>(
                            reference.NativeTopology.Entities.Select(entity => entity.Id), StringComparer.Ordinal);
                        var staleAreas = game.State.AreaEffects
                            .Where(area => !expectedEntities.Contains(area.UniqueId)).ToList();
                        foreach (var area in staleAreas)
                        {
                            if (!RealTimeNativeAreaRetirement.CanRetire(area))
                                throw new InvalidOperationException("native-reference-extra-area-not-retirable:" + area.UniqueId);
                            transaction.SuspendNativeArea(area, game.State);
                            transaction.RemoveArea(EntityService.Instance, area.HoldingState, area);
                            TrackAreaPools(transaction, area, removed: true);
                        }
                        if (staleAreas.Count != 0)
                            _logger.LogInformation("Retired stale same-area native effects before reference verification. Count={Count}, Ids={Ids}",
                                staleAreas.Count, string.Join(",", staleAreas.Select(area => area.UniqueId)));
                        var expectedTopology = reference.NativeTopology.Entities.ToDictionary(
                            entity => entity.Id, StringComparer.Ordinal);
                        NormalizePostLoadOnlyEncumbranceParts(reference.NativeTopology,
                            game.State.Units.All, "restore");
                        string ComponentSignature(Networking.Messages.Contracts.NetworkNativeComponentTopology component) =>
                            component.RuntimeType + "@" + component.SourceName;
                        string FactSignature(Networking.Messages.Contracts.NetworkNativeFactTopology fact) =>
                            fact.RuntimeType + "@" + fact.BlueprintId + "[" + string.Join("|",
                                fact.Components.Select(ComponentSignature).OrderBy(value => value, StringComparer.Ordinal)) + "]";
                        string NativeFactSignature(EntityFact fact) => fact.GetType().FullName + "@"
                            + fact.Blueprint.AssetGuid + "[" + string.Join("|", fact.Components.Select(component =>
                                component.GetType().FullName + "@" + (component.SourceBlueprintComponentName ?? ""))
                                .OrderBy(value => value, StringComparer.Ordinal)) + "]";

                        // A native same-area load can recreate a mechanically
                        // identical buff under a new runtime ID. Topology is
                        // intentionally ID-agnostic, while temporal buff updates
                        // address the authoritative host ID. Rebind only an
                        // unmatched, semantically identical native buff to a
                        // missing host ID; unresolved cases are reconstructed
                        // below from the full confirmed NetworkBuff state.
                        var reboundBuffIds = new System.Collections.Generic.List<string>();
                        foreach (var unit in game.State.Units.All)
                        {
                            if (!expectedTopology.TryGetValue(unit.UniqueId, out var expectedEntity)) continue;
                            var expectedBuffs = expectedEntity.Facts
                                .Where(fact => fact.RuntimeType == typeof(Buff).FullName).ToList();
                            var expectedIds = new System.Collections.Generic.HashSet<string>(
                                expectedBuffs.Select(fact => fact.Id), StringComparer.OrdinalIgnoreCase);
                            var nativeBuffs = unit.Facts.List.OfType<Buff>().ToList();
                            var nativeIds = new System.Collections.Generic.HashSet<string>(
                                nativeBuffs.Select(buff => buff.UniqueId), StringComparer.OrdinalIgnoreCase);
                            var substitutes = nativeBuffs.Where(buff => !expectedIds.Contains(buff.UniqueId)).ToList();
                            foreach (var expectedBuff in expectedBuffs.Where(fact => !nativeIds.Contains(fact.Id)))
                            {
                                var signature = FactSignature(expectedBuff);
                                var substitute = substitutes.FirstOrDefault(buff => string.Equals(
                                    NativeFactSignature(buff), signature, StringComparison.Ordinal));
                                if (substitute == null) continue;
                                var previousId = substitute.UniqueId;
                                substitute.UniqueId = expectedBuff.Id;
                                if (!string.Equals(substitute.UniqueId, expectedBuff.Id,
                                        StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidOperationException("native-reference-buff-id-rebind-failed:"
                                        + unit.UniqueId + ":" + expectedBuff.Id);
                                substitutes.Remove(substitute);
                                nativeIds.Add(expectedBuff.Id);
                                reboundBuffIds.Add(unit.UniqueId + "@" + previousId + "->" + expectedBuff.Id);
                            }
                        }
                        if (reboundBuffIds.Count != 0)
                            _logger.LogInformation(
                                "Rebound same-area native buff IDs to the host checkpoint. Count={Count}, Buffs={Buffs}",
                                reboundBuffIds.Count, string.Join(",", reboundBuffIds.Take(32)));

                        // Native PostLoad can materialize blueprint abilities that
                        // do not exist in the authoritative host graph, or can
                        // duplicate one that does. Ability facts are detachable
                        // native facts, so make their exact semantic multiplicity
                        // match the host before topology verification.
                        var removedDuplicateAbilities = new System.Collections.Generic.List<string>();
                        foreach (var unit in game.State.Units.All)
                        {
                            if (!expectedTopology.TryGetValue(unit.UniqueId, out var expectedEntity)) continue;
                            var expectedAbilityCounts = expectedEntity.Facts
                                .Where(fact => fact.RuntimeType == typeof(Ability).FullName)
                                .GroupBy(FactSignature, StringComparer.Ordinal)
                                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                            foreach (var group in unit.Facts.List.OfType<Ability>()
                                         .GroupBy(NativeFactSignature, StringComparer.Ordinal).ToList())
                            {
                                var expectedCount = expectedAbilityCounts.TryGetValue(group.Key, out var count)
                                    ? count : 0;
                                if (group.Count() <= expectedCount) continue;
                                foreach (var duplicate in group.Skip(expectedCount).ToList())
                                {
                                    var marker = unit.UniqueId + "@" + duplicate.Blueprint.AssetGuid;
                                    unit.Facts.Remove(duplicate);
                                    if (unit.Facts.Contains(duplicate))
                                        throw new InvalidOperationException(
                                            "native-reference-extra-ability-removal-failed:" + marker);
                                    removedDuplicateAbilities.Add(marker);
                                }
                            }
                        }
                        if (removedDuplicateAbilities.Count != 0)
                            _logger.LogInformation(
                                "Removed duplicate abilities introduced by native client load. Count={Count}, Abilities={Abilities}",
                                removedDuplicateAbilities.Count, string.Join(",", removedDuplicateAbilities));
                        var afterCleanup = RealTimeNativeGraphInspection.CaptureState(
                            game.State.Units.All, game.State.AreaEffects, canApply);
                        var actualEntities = afterCleanup.Entities.ToDictionary(entity => entity.Id, StringComparer.Ordinal);
                        var nativeUnitsById = game.State.Units.All.ToDictionary(unit => unit.UniqueId, StringComparer.Ordinal);
                        var expectedUnitIds = new System.Collections.Generic.HashSet<string>(
                            reference.NativeTopology.Entities.Where(entity => entity.Kind == "unit")
                                .Select(entity => entity.Id), StringComparer.Ordinal);
                        var nativeReferenceUnits = reference.NativeUnits ?? [];
                        if (nativeReferenceUnits.Count != expectedUnitIds.Count
                            || nativeReferenceUnits.Any(unit => unit?.Id == null
                                || unit.BuffCollection?.Buffs == null
                                || unit.BuffCollection.NegativeLevels == null
                                || !expectedUnitIds.Contains(unit.Id))
                            || nativeReferenceUnits.Select(unit => unit.Id)
                                .Distinct(StringComparer.Ordinal).Count() != nativeReferenceUnits.Count)
                            throw new InvalidOperationException("native-reference-unit-buff-inventory-invalid");
                        var referenceUnitsById = nativeReferenceUnits.ToDictionary(
                            unit => unit.Id, StringComparer.Ordinal);
                        var factsBeforeRestoringFacts = new System.Collections.Generic.HashSet<EntityFact>(
                            game.State.Units.All.SelectMany(unit => unit.Facts.List));
                        var restoredMarkers = new System.Collections.Generic.List<string>();
                        // Restore structural features before transient buffs. A
                        // feature's normal attach path can create or re-parent a
                        // derived buff (PowerAttackBuffEffect is one example).
                        // Restoring the buff first made its context correct only
                        // until a later feature attachment mutated the graph.
                        using (RealTimePresentationRuntime.BeginAuthoritativePresentation())
                            foreach (var expectedEntity in reference.NativeTopology.Entities.Where(entity => entity.Kind == "unit"))
                            {
                                if (!actualEntities.TryGetValue(expectedEntity.Id, out var actualEntity)
                                    || !nativeUnitsById.TryGetValue(expectedEntity.Id, out var unit))
                                    continue;
                                var unmatched = actualEntity.Facts.Select(FactSignature).ToList();
                                foreach (var expectedFact in expectedEntity.Facts)
                                {
                                    var signature = FactSignature(expectedFact);
                                    var match = unmatched.FindIndex(value => string.Equals(value, signature, StringComparison.Ordinal));
                                    if (match >= 0) { unmatched.RemoveAt(match); continue; }
                                    if (expectedFact.RuntimeType == typeof(Buff).FullName) continue;
                                    if (expectedFact.RuntimeType != typeof(Feature).FullName || expectedFact.Components.Count != 0)
                                        throw new InvalidOperationException("native-reference-missing-fact-not-reconstructible:"
                                            + expectedEntity.Id + ":" + expectedFact.RuntimeType + ":" + expectedFact.BlueprintId);
                                    var blueprint = ResourcesLibrary.TryGetBlueprint<BlueprintFeature>(expectedFact.BlueprintId);
                                    if (blueprint == null)
                                        throw new InvalidOperationException("native-reference-missing-feature-blueprint-unavailable:"
                                            + expectedEntity.Id + ":" + expectedFact.BlueprintId);
                                    var restored = unit.Descriptor.AddFact(blueprint);
                                    if (restored == null || restored.GetType() != typeof(Feature)
                                        || restored.Blueprint?.AssetGuid.ToString() != expectedFact.BlueprintId
                                        || restored.Components == null || restored.Components.Count != 0)
                                        throw new InvalidOperationException("native-reference-missing-feature-restoration-failed:"
                                            + expectedEntity.Id + ":" + expectedFact.BlueprintId);
                                    restoredMarkers.Add(expectedEntity.Id + "@" + expectedFact.BlueprintId);
                                }
                            }

                        // Features restored above may have materialized a missing
                        // derived buff with a fresh runtime ID. Rebind those native
                        // instances first, then construct only the buffs that are
                        // still genuinely absent. No further feature attachment is
                        // allowed after this point, so the confirmed context chain
                        // remains stable through verification.
                        var reboundPostFeatureBuffIds = new System.Collections.Generic.List<string>();
                        using (RealTimePresentationRuntime.BeginAuthoritativePresentation())
                            foreach (var expectedEntity in reference.NativeTopology.Entities.Where(entity => entity.Kind == "unit"))
                            {
                                if (!nativeUnitsById.TryGetValue(expectedEntity.Id, out var unit)
                                    || !referenceUnitsById.TryGetValue(expectedEntity.Id, out var referenceUnit))
                                    throw new InvalidOperationException("native-reference-missing-buff-unit-unavailable:"
                                        + expectedEntity.Id);
                                var expectedBuffs = expectedEntity.Facts
                                    .Where(fact => fact.RuntimeType == typeof(Buff).FullName).ToList();
                                var expectedIds = new System.Collections.Generic.HashSet<string>(
                                    expectedBuffs.Select(fact => fact.Id), StringComparer.OrdinalIgnoreCase);
                                var nativeBuffs = unit.Facts.List.OfType<Buff>().ToList();
                                var nativeIds = new System.Collections.Generic.HashSet<string>(
                                    nativeBuffs.Select(buff => buff.UniqueId), StringComparer.OrdinalIgnoreCase);
                                var substitutes = nativeBuffs.Where(buff => !expectedIds.Contains(buff.UniqueId)).ToList();
                                foreach (var expectedBuff in expectedBuffs.Where(fact => !nativeIds.Contains(fact.Id)))
                                {
                                    var signature = FactSignature(expectedBuff);
                                    var substitute = substitutes.FirstOrDefault(buff => string.Equals(
                                        NativeFactSignature(buff), signature, StringComparison.Ordinal));
                                    if (substitute != null)
                                    {
                                        var previousId = substitute.UniqueId;
                                        substitute.UniqueId = expectedBuff.Id;
                                        substitutes.Remove(substitute);
                                        nativeIds.Add(expectedBuff.Id);
                                        reboundPostFeatureBuffIds.Add(expectedEntity.Id + "@" + previousId + "->" + expectedBuff.Id);
                                        continue;
                                    }
                                    var confirmed = referenceUnit.BuffCollection.Buffs.FirstOrDefault(buff =>
                                        string.Equals(buff.Id, expectedBuff.Id, StringComparison.OrdinalIgnoreCase)
                                        && string.Equals(buff.BlueprintId, expectedBuff.BlueprintId,
                                            StringComparison.OrdinalIgnoreCase));
                                    if (confirmed == null)
                                        throw new InvalidOperationException("native-reference-missing-buff-state-unavailable:"
                                            + expectedEntity.Id + ":" + expectedBuff.BlueprintId);
                                    var restoredBuff = _buffInteractionService.RestoreNativeBuffForCheckpoint(unit, confirmed);
                                    transaction.TrackNativeBuff(restoredBuff, () => unit.Buffs.UpdateNextEvent());
                                    nativeIds.Add(expectedBuff.Id);
                                    restoredMarkers.Add(expectedEntity.Id + "@buff:" + expectedBuff.BlueprintId);
                                }
                            }
                        if (reboundPostFeatureBuffIds.Count != 0)
                            _logger.LogInformation(
                                "Rebound native buffs materialized while restoring host features. Count={Count}, Buffs={Buffs}",
                                reboundPostFeatureBuffIds.Count, string.Join(",", reboundPostFeatureBuffIds.Take(32)));
                        if (restoredMarkers.Count != 0)
                            _logger.LogInformation("Restored host facts omitted by native same-area load. Count={Count}, Facts={Facts}",
                                restoredMarkers.Count, string.Join(",", restoredMarkers.Take(32)));

                        // Adding an omitted native feature can cause the game's
                        // normal feature attach path to grant an ability that the
                        // same-area load already materialized. Normalize that
                        // post-attach surplus as well, with the same strict rule:
                        // only duplicates above a non-zero host multiplicity.
                        var removedPostRestoreAbilities = new System.Collections.Generic.List<string>();
                        foreach (var unit in game.State.Units.All)
                        {
                            if (!expectedTopology.TryGetValue(unit.UniqueId, out var expectedEntity)) continue;
                            var expectedAbilityCounts = expectedEntity.Facts
                                .Where(fact => fact.RuntimeType == typeof(Ability).FullName)
                                .GroupBy(FactSignature, StringComparer.Ordinal)
                                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                            var expectedAbilityIds = expectedEntity.Facts
                                .Where(fact => fact.RuntimeType == typeof(Ability).FullName)
                                .GroupBy(FactSignature, StringComparer.Ordinal)
                                .ToDictionary(group => group.Key, group => (System.Collections.Generic.ISet<string>)
                                    new System.Collections.Generic.HashSet<string>(group.Select(fact => fact.Id),
                                        StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
                            foreach (var group in unit.Facts.List.OfType<Ability>()
                                         .GroupBy(NativeFactSignature, StringComparer.Ordinal).ToList())
                            {
                                var expectedCount = expectedAbilityCounts.TryGetValue(group.Key, out var count)
                                    ? count : 0;
                                var excess = group.Count() - expectedCount;
                                if (excess <= 0) continue;
                                // Host-absent abilities are removable only when
                                // this transaction itself just created them as a
                                // side effect of attaching a missing host feature.
                                // Any pre-existing client-only fact remains fatal.
                                foreach (var duplicate in TakeTransactionIntroducedSurplus(
                                             group, factsBeforeRestoringFacts, expectedCount,
                                             expectedAbilityIds.TryGetValue(group.Key, out var ids) ? ids : null))
                                {
                                    var marker = unit.UniqueId + "@" + duplicate.Blueprint.AssetGuid;
                                    unit.Facts.Remove(duplicate);
                                    if (unit.Facts.Contains(duplicate))
                                        throw new InvalidOperationException(
                                            "native-reference-post-feature-extra-ability-removal-failed:" + marker);
                                    removedPostRestoreAbilities.Add(marker);
                                }
                            }
                        }
                        if (removedPostRestoreAbilities.Count != 0)
                            _logger.LogInformation(
                                "Removed duplicate abilities introduced while restoring native host features. Count={Count}, Abilities={Abilities}",
                                removedPostRestoreAbilities.Count, string.Join(",", removedPostRestoreAbilities));

                        // A restored buff can replay one of its attach components (for example
                        // AddCondition) and create a second, host-absent buff. Remove only facts
                        // created by this reconstruction transaction and only above the exact host
                        // multiplicity. Pre-existing client-only facts remain a fatal topology error.
                        var removedPostRestoreBuffs = new System.Collections.Generic.List<string>();
                        foreach (var unit in game.State.Units.All)
                        {
                            if (!expectedTopology.TryGetValue(unit.UniqueId, out var expectedEntity)) continue;
                            var expectedBuffCounts = expectedEntity.Facts
                                .Where(fact => fact.RuntimeType == typeof(Buff).FullName)
                                .GroupBy(FactSignature, StringComparer.Ordinal)
                                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                            var expectedBuffIds = expectedEntity.Facts
                                .Where(fact => fact.RuntimeType == typeof(Buff).FullName)
                                .GroupBy(FactSignature, StringComparer.Ordinal)
                                .ToDictionary(group => group.Key, group => (System.Collections.Generic.ISet<string>)
                                    new System.Collections.Generic.HashSet<string>(group.Select(fact => fact.Id),
                                        StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
                            foreach (var group in unit.Facts.List.OfType<Buff>()
                                         .GroupBy(NativeFactSignature, StringComparer.Ordinal).ToList())
                            {
                                var expectedCount = expectedBuffCounts.TryGetValue(group.Key, out var count)
                                    ? count : 0;
                                foreach (var extra in TakeTransactionIntroducedSurplus(
                                             group, factsBeforeRestoringFacts, expectedCount,
                                             expectedBuffIds.TryGetValue(group.Key, out var ids) ? ids : null))
                                {
                                    var marker = unit.UniqueId + "@" + extra.Blueprint.AssetGuid;
                                    unit.Facts.Remove(extra);
                                    if (unit.Facts.Contains(extra))
                                        throw new InvalidOperationException(
                                            "native-reference-post-restore-extra-buff-removal-failed:" + marker);
                                    removedPostRestoreBuffs.Add(marker);
                                }
                            }
                        }
                        if (removedPostRestoreBuffs.Count != 0)
                            _logger.LogInformation(
                                "Removed host-absent buffs introduced while restoring native facts. Count={Count}, Buffs={Buffs}",
                                removedPostRestoreBuffs.Count, string.Join(",", removedPostRestoreBuffs));

                        // UniqueBuff maintains a derived caster-side index. A
                        // directly restored native buff has all of its mechanical
                        // components but does not replay OnActivate, so rebuild
                        // only this deterministic index when the host topology
                        // proves that the part must exist.
                        var uniqueBuffPartType = typeof(UnitPartUniqueBuffs).FullName;
                        var rebuiltUniqueBuffParts = new System.Collections.Generic.List<string>();
                        foreach (var unit in game.State.Units.All)
                        {
                            if (!expectedTopology.TryGetValue(unit.UniqueId, out var expectedEntity)
                                || !expectedEntity.Parts.Contains(uniqueBuffPartType, StringComparer.Ordinal)
                                || unit.Get<UnitPartUniqueBuffs>() != null)
                                continue;
                            var part = unit.Ensure<UnitPartUniqueBuffs>();
                            var indexedBuffs = game.State.Units.All
                                .SelectMany(owner => owner.Buffs.Enumerable)
                                .Where(buff => buff?.Context?.MaybeCaster != null
                                    && string.Equals(buff.Context.MaybeCaster.UniqueId, unit.UniqueId,
                                        StringComparison.Ordinal)
                                    && buff.Components.OfType<UniqueBuff>().Any())
                                .GroupBy(buff => buff.Blueprint.AssetGuid.ToString(), StringComparer.OrdinalIgnoreCase)
                                .Select(group => group.Last()).ToList();
                            part.Buffs.Clear();
                            part.Buffs.AddRange(indexedBuffs);
                            transaction.TrackNativeState(
                                () =>
                                {
                                    var current = unit.Get<UnitPartUniqueBuffs>();
                                    if (!ReferenceEquals(current, part)
                                        || indexedBuffs.Any(buff => !current.Buffs.Contains(buff)))
                                        throw new InvalidOperationException(
                                            "native-unique-buff-part-changed-before-commit:" + unit.UniqueId);
                                },
                                () =>
                                {
                                    if (ReferenceEquals(unit.Get<UnitPartUniqueBuffs>(), part))
                                        unit.Remove<UnitPartUniqueBuffs>();
                                });
                            rebuiltUniqueBuffParts.Add(unit.UniqueId + "@" + indexedBuffs.Count);
                        }
                        if (rebuiltUniqueBuffParts.Count != 0)
                            _logger.LogInformation(
                                "Rebuilt derived unique-buff caster indexes after native load. Count={Count}, Parts={Parts}",
                                rebuiltUniqueBuffParts.Count, string.Join(",", rebuiltUniqueBuffParts));

                        // UnitPartFerocity is a short-lived, derived combat part.
                        // Owlcat serializes it in the native archive but can drop
                        // it during same-area PostLoad even though the associated
                        // ferocity condition is still part of the authoritative
                        // combat instant. Its only private state is the start
                        // game-time; native loading is protected by a paused
                        // world, so Init recreates the exact checkpoint instant
                        // and its staggered condition. Do not generalize this to
                        // arbitrary parts: most carry state that topology alone
                        // cannot reconstruct safely.
                        var ferocityPartType = typeof(UnitPartFerocity).FullName;
                        var rebuiltFerocityParts = new System.Collections.Generic.List<string>();
                        foreach (var unit in game.State.Units.All)
                        {
                            if (!expectedTopology.TryGetValue(unit.UniqueId, out var expectedEntity)
                                || !expectedEntity.Parts.Contains(ferocityPartType, StringComparer.Ordinal)
                                || unit.Get<UnitPartFerocity>() != null)
                                continue;
                            var part = unit.Ensure<UnitPartFerocity>();
                            part.Init();
                            transaction.TrackNativeState(
                                () =>
                                {
                                    if (!ReferenceEquals(unit.Get<UnitPartFerocity>(), part))
                                        throw new InvalidOperationException(
                                            "native-ferocity-part-changed-before-commit:" + unit.UniqueId);
                                },
                                () =>
                                {
                                    if (ReferenceEquals(unit.Get<UnitPartFerocity>(), part))
                                        unit.Remove<UnitPartFerocity>();
                                });
                            rebuiltFerocityParts.Add(unit.UniqueId);
                        }
                        if (rebuiltFerocityParts.Count != 0)
                            _logger.LogInformation(
                                "Rebuilt derived ferocity parts after native load. Count={Count}, Units={Units}",
                                rebuiltFerocityParts.Count, string.Join(",", rebuiltFerocityParts));

                        // Some native attach processors defer AddCondition until
                        // the end of the native load or the current reconstruction
                        // tick. Such a buff can already exist when this transaction
                        // starts, even though it is absent from the host's complete
                        // native buff inventory. Perform a final authoritative sweep
                        // immediately before topology proof. At this point the world
                        // was wholly replaced from the host archive and every host
                        // buff is represented in expectedTopology/nativeReferenceUnits,
                        // so surplus Buff facts are detachable load side effects;
                        // arbitrary non-Buff facts remain strictly fatal.
                        var removedLateBuffs = new System.Collections.Generic.List<string>();
                        foreach (var unit in game.State.Units.All)
                        {
                            if (!expectedTopology.TryGetValue(unit.UniqueId, out var expectedEntity)) continue;
                            var expectedGroups = expectedEntity.Facts
                                .Where(fact => fact.RuntimeType == typeof(Buff).FullName)
                                .GroupBy(FactSignature, StringComparer.Ordinal)
                                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
                            foreach (var group in unit.Facts.List.OfType<Buff>()
                                         .GroupBy(NativeFactSignature, StringComparer.Ordinal).ToList())
                            {
                                var expectedFacts = expectedGroups.TryGetValue(group.Key, out var values)
                                    ? values : [];
                                var expectedIdsForGroup = (System.Collections.Generic.ISet<string>)
                                    new System.Collections.Generic.HashSet<string>(expectedFacts.Select(fact => fact.Id),
                                        StringComparer.OrdinalIgnoreCase);
                                foreach (var extra in TakeAuthoritativeSurplus(
                                             group, expectedFacts.Count, expectedIdsForGroup))
                                {
                                    var marker = unit.UniqueId + "@" + extra.Blueprint.AssetGuid;
                                    unit.Facts.Remove(extra);
                                    if (unit.Facts.Contains(extra))
                                        throw new InvalidOperationException(
                                            "native-reference-late-extra-buff-removal-failed:" + marker);
                                    removedLateBuffs.Add(marker);
                                }
                            }
                        }
                        if (removedLateBuffs.Count != 0)
                            _logger.LogInformation(
                                "Removed host-absent buffs introduced by native load or reconstruction. Count={Count}, Buffs={Buffs}",
                                removedLateBuffs.Count, string.Join(",", removedLateBuffs.Take(32)));
                        RequireExpectedTopology();
                        var participants = RealTimeEncounterParticipants<UnitEntityData>.For(Main.Multiplayer?.RealTimeCombatIdentity)
                            ?? throw new InvalidOperationException("native-reference-encounter-missing");
                        var nativeUnits = game.State.Units.All.ToDictionary(unit => unit.UniqueId, StringComparer.Ordinal);
                        using var rebound = participants.RebindCheckpoint(reference.Units.Select(unit => unit?.Id), id =>
                            nativeUnits.TryGetValue(id, out var unit) ? unit : null, canApply);
                        // These are all valid objects of the NEW native world. A
                        // later data-restore failure must not put disposed objects
                        // from the OLD world back into the encounter registry.
                        rebound.Commit();
                        RequireCurrentWorld();
                        var normalizedSpellSlots = 0;
                        foreach (var networkUnit in reference.Units.Where(unit =>
                                     unit?.Descriptor?.SpellbooksCaptured == true))
                        {
                            if (!nativeUnits.TryGetValue(networkUnit.Id, out var nativeUnit))
                                throw new InvalidOperationException(
                                    "native-spellbook-unit-unavailable:" + networkUnit.Id);
                            normalizedSpellSlots += RealTimeSpellbookReplication.NormalizeNativeLoadTopology(
                                nativeUnit, networkUnit.Descriptor.Spellbooks, transaction);
                        }
                        if (normalizedSpellSlots != 0)
                            _logger.LogInformation(
                                "Normalized prepared spell topology after native load. ChangedSlots={ChangedSlots}",
                                normalizedSpellSlots);
                        ApplyRealTimeCombatState(reference, transaction, requireNativeEntities: true);
                        RequireCurrentWorld();
                        var appliedTopology = RequireExpectedTopology();
                        applied = GetRealTimeCombatState();
                        applied.NativeTopology = appliedTopology;
                        var difference = RealTimeCombatTemporalDiff.Summarize(reference, applied);
                        if (difference != null || RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = reference })
                            != RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = applied }))
                            throw new InvalidOperationException("native-reference-state-differs:" + difference);
                            transaction.VerifyAndCommit(reference, applied);
                            // The verified replacement graph no longer contains
                            // the retired stale client effects. Resolve their
                            // external repair markers now, leaving only the
                            // native-load lease for the separate Commit step.
                            transaction.ResolveVerifiedNativeGraphObligations();
                        }
                        catch (Exception primaryFailure)
                        {
                            // IDisposable rollback is deliberately strict and can
                            // itself fail when the protected world changed. Keep
                            // the original reconstruction cause visible in the
                            // log even if the rollback exception becomes the one
                            // propagated by the C# using/finally lowering.
                            _logger.LogError(primaryFailure,
                                "Native reference reconstruction failed before rollback.");
                            throw;
                        }
                    }
                    RequireCurrentWorld();
                    completion.TrySetResult(applied);
                }
                catch (Exception error) { completion.TrySetException(error); }
            });
            return completion.Task;
        }

        internal static System.Collections.Generic.List<T> TakeTransactionIntroducedSurplus<T>(
            System.Collections.Generic.IEnumerable<T> current,
            System.Collections.Generic.ISet<EntityFact> before,
            int expectedCount,
            System.Collections.Generic.ISet<string> expectedIds = null) where T : EntityFact
        {
            var values = current?.ToList() ?? [];
            var excess = Math.Max(0, values.Count - Math.Max(0, expectedCount));
            if (excess == 0) return [];
            return values.Where(value => !before.Contains(value))
                .OrderBy(value => expectedIds?.Contains(value.UniqueId) == true ? 1 : 0)
                .Take(excess).ToList();
        }

        private void NormalizePostLoadOnlyEncumbranceParts(NetworkNativeTopology topology,
            System.Collections.Generic.IEnumerable<UnitEntityData> units, string phase)
        {
            if (topology?.Entities == null || units == null)
                throw new InvalidOperationException("native-reference-topology-missing");
            var encumbrancePartType = typeof(UnitPartEncumbrance).FullName;
            var expectedTopology = topology.Entities.ToDictionary(entity => entity.Id, StringComparer.Ordinal);
            var normalized = new System.Collections.Generic.List<string>();
            foreach (var unit in units)
            {
                if (!expectedTopology.TryGetValue(unit.UniqueId, out var expectedEntity)
                    || expectedEntity.Parts.Contains(encumbrancePartType, StringComparer.Ordinal)
                    || unit.Descriptor.Get<UnitPartEncumbrance>() == null)
                    continue;
                var calculated = Game.Instance.State.LoadedAreaState.Settings.IgnorePersonalEncumbrance
                    ? Encumbrance.Light
                    : EncumbranceHelper.GetCarryingCapacity(unit.Descriptor).GetEncumbrance();
                _logger.LogInformation(
                    "Resetting post-load-only encumbrance part to the authoritative host topology. Phase={Phase}, Unit={Unit}, Loaded={Loaded}, Calculated={Calculated}",
                    phase, unit.UniqueId, unit.Descriptor.Encumbrance, calculated);
                unit.Descriptor.Encumbrance = Encumbrance.Light;
                unit.Descriptor.Remove<UnitPartEncumbrance>();
                if (unit.Descriptor.Get<UnitPartEncumbrance>() != null)
                    throw new InvalidOperationException(
                        "native-reference-extra-encumbrance-part-removal-failed:" + unit.UniqueId);
                normalized.Add(unit.UniqueId);
            }
            if (normalized.Count != 0)
                _logger.LogInformation(
                    "Removed post-load-only encumbrance parts absent from the host topology. Phase={Phase}, Count={Count}, Ids={Ids}",
                    phase, normalized.Count, string.Join(",", normalized));
        }

        internal static System.Collections.Generic.List<T> TakeAuthoritativeSurplus<T>(
            System.Collections.Generic.IEnumerable<T> current,
            int expectedCount,
            System.Collections.Generic.ISet<string> expectedIds = null) where T : EntityFact
        {
            var values = current?.ToList() ?? [];
            var excess = Math.Max(0, values.Count - Math.Max(0, expectedCount));
            if (excess == 0) return [];
            return values.OrderBy(value => expectedIds?.Contains(value.UniqueId) == true ? 1 : 0)
                .Take(excess).ToList();
        }
    }
}
