using System;
using System.Linq;
using System.Threading.Tasks;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence;
using Kingmaker.GameModes;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking;
using NetworkNativeTopology = WOTRMultiplayer.Networking.Messages.Contracts.NetworkNativeTopology;

namespace WOTRMultiplayer.Services.GameInteraction
{
    public partial class CombatInteractionService
    {
        public NetworkCombatState GetRealTimeNativeReferenceState()
        {
            var game = Game.Instance;
            if (!RealTimePresentationRuntime.Enabled || RealTimePresentationRuntime.IsClientActive
                || game?.State == null || game.Player == null || !game.IsPaused || RealTimeNativeLoadLease.IsActive
                || game.SaveManager.CurrentState != SaveManager.State.None || game.SaveManager.CommitInProgress
                || LoadingProcess.Instance.IsLoadingInProcess || game.EntityCreator.CreationQueue.Any()
                || game.CurrentMode != GameModeType.Default && game.CurrentMode != GameModeType.Pause
                || IsInCrusadeTacticalCombat() || game.Player?.StartPreset?.Campaign?.DlcReward != null)
                throw new InvalidOperationException("native-reference-host-not-at-safe-point");
            RealTimeNativeCharacterUiGuard.RequireIdle(game);
            var world = game.State;
            var state = GetRealTimeCombatState();
            state.NativeTopology = RealTimeNativeGraphInspection.CaptureState(world.Units.All, world.AreaEffects,
                () => ReferenceEquals(Game.Instance, game) && ReferenceEquals(game.State, world) && game.IsPaused);
            return state;
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
                    NetworkCombatState applied;
                    using (var transaction = new RealTimeBuffRestoreTransaction())
                    {
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
                    }
                    RequireCurrentWorld();
                    completion.TrySetResult(applied);
                }
                catch (Exception error) { completion.TrySetException(error); }
            });
            return completion.Task;
        }
    }
}
