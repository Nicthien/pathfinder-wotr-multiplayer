using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Kingmaker;
using Kingmaker.Blueprints.Area;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using Kingmaker.ElementsSystem;
using Kingmaker.GameModes;
using Kingmaker.UI.Models.Log;
using Kingmaker.Utility;
using Newtonsoft.Json;
using Owlcat.Runtime.Visual.RenderPipeline.RendererFeatures.FogOfWar;
using UnityEngine.Rendering;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Abstractions.Unity;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Runtime entry point only. Do not call from offline probes: native PreSave
    // requires a running world. No SaveRoutine, player slot, screenshot or cloud
    // save is used. A barrier owner must retain pause until transfer/load/ACK.
    internal static class RealTimeNativeHostCapture
    {
        internal static Task<RealTimeMemorySaver> CaptureAsync(IMainThreadAccessor mainThread,
            ICombatInteractionService combat, string campaignId, string checkpointId, Func<bool> isAuthorized)
            => CaptureCoreAsync(mainThread, combat, campaignId, checkpointId, isAuthorized, false);

        internal static Task<RealTimeMemorySaver> CaptureTurnBasedAsync(IMainThreadAccessor mainThread,
            ICombatInteractionService combat, string campaignId, string checkpointId, Func<bool> isAuthorized)
            => CaptureCoreAsync(mainThread, combat, campaignId, checkpointId, isAuthorized, true);

        private static Task<RealTimeMemorySaver> CaptureCoreAsync(IMainThreadAccessor mainThread,
            ICombatInteractionService combat, string campaignId, string checkpointId, Func<bool> isAuthorized, bool turnBased)
        {
            var result = new TaskCompletionSource<RealTimeMemorySaver>(TaskCreationOptions.RunContinuationsAsynchronously);
            mainThread.Post(() =>
            {
                try
                {
                    RequireReady(combat, campaignId, checkpointId, isAuthorized);
                    if (turnBased) CombatSaving.TurnBasedSaveNativeState.RequireReady();
                    var game = Game.Instance;
                    var world = game.State;
                    var player = game.Player;
                    var area = world.LoadedAreaState;
                    var part = AreaService.Instance.CurrentAreaPart;
                    var time = game.TimeController.GameTime;
                    var fog = FogOfWarArea.Active;
                    bool SameWorld() => isAuthorized() && ReferenceEquals(Game.Instance, game)
                        && ReferenceEquals(game.State, world) && ReferenceEquals(game.Player, player)
                        && ReferenceEquals(world.LoadedAreaState, area) && AreaService.Instance.CurrentAreaPart == part
                        && game.TimeController.GameTime == time && CombatSaving.CombatSavePauseLease.IsWorldPaused(game);
                    if (fog != null)
                    {
                        var pending = typeof(FogOfWarArea).GetField("m_DataRequest", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException("native-checkpoint-fog-request-layout-unsupported");
                        if (pending.GetValue(fog) != null)
                            throw new InvalidOperationException("native-checkpoint-fog-readback-already-owned");
                    }
                    var wait = fog != null ? fog.RequestDataCoroutine() : null;
                    CoroutineRunner.Start(FinishAfterFog(wait, SameWorld, () =>
                    {
                        RequireReady(combat, campaignId, checkpointId, isAuthorized);
                        var nativeTurn = turnBased ? CombatSaving.TurnBasedSaveNativeState.Capture() : null;
                        if (!SameWorld() || FogOfWarArea.Active != fog)
                            throw new InvalidOperationException("native-checkpoint-world-changed-during-fog");
                        byte[] freshFog = null;
                        string fogScene = null;
                        if (fog != null)
                        {
                            // The native coroutine logs GPU failure but leaves
                            // its previous m_Data value in place. Inspect our
                            // completed request rather than read stale/freed data.
                            var requestField = wait.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                .SingleOrDefault(field => field.FieldType == typeof(AsyncGPUReadbackRequest))
                                ?? throw new InvalidOperationException("native-checkpoint-fog-result-layout-unsupported");
                            var request = (AsyncGPUReadbackRequest)requestField.GetValue(wait);
                            if (!request.done || request.hasError)
                                throw new InvalidOperationException("native-checkpoint-fog-readback-failed");
                            fogScene = fog.gameObject.scene.name;
                            var data = fog.GetData();
                            try
                            {
                                if (!data.IsCreated || data.Length == 0 || data.Length > RealTimeMemorySaver.MaximumFileBytes)
                                    throw new InvalidDataException("native-checkpoint-size-limit");
                                freshFog = data.ToArray();
                            }
                            finally { if (data.IsCreated) data.Dispose(); }
                        }
                        var before = CaptureMechanicalStateForVerification(combat, turnBased);
                        var beforeHash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = before });
                        RealTimeMemorySaver source;
                        try { source = CaptureNow(game, checkpointId, freshFog, fogScene, SameWorld, () =>
                        {
                            if (!SameWorld()) throw new InvalidOperationException("native-checkpoint-world-identity-or-clock-changed");
                            var after = CaptureMechanicalStateForVerification(combat, turnBased);
                            if (RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = after }) != beforeHash)
                            {
                                if (CombatSaving.CombatSaveActivation.Enabled)
                                {
                                    File.WriteAllText(Path.Combine(ApplicationPaths.persistentDataPath, "combat-save-capture-before.json"), JsonConvert.SerializeObject(before));
                                    File.WriteAllText(Path.Combine(ApplicationPaths.persistentDataPath, "combat-save-capture-after.json"), JsonConvert.SerializeObject(after));
                                }
                                throw new InvalidOperationException("native-checkpoint-mechanical-state-changed");
                            }
                            var temporal = RealTimeCombatTemporalDiff.Summarize(before, after);
                            if (temporal != null) throw new InvalidOperationException("native-checkpoint-temporal-state-changed:" + temporal);
                            if (turnBased && CombatSaving.TurnBasedSaveNativeState.Capture() != nativeTurn)
                                throw new InvalidOperationException("native-checkpoint-turn-state-changed");
                            return true;
                        }, nativeTurn); }
                        catch
                        {
                            // PreSave callbacks may swallow their own exception;
                            // never let a partially prepared host silently resume
                            // or write a save after failed lifecycle verification.
                            if (ReferenceEquals(game.State, world))
                            { game.IsPaused = true; RealTimeNativeGraphRepair.Add(world, new object()); }
                            throw;
                        }
                        try
                        {
                            // Decode the real native header before publishing. No
                            // world disposal or load is attempted at this stage.
                            var header = RealTimeNativeCheckpointLoadSource.Prepare(source, campaignId, checkpointId);
                            header.Saver.Dispose();
                            if (!SameWorld()) throw new InvalidOperationException("native-checkpoint-host-changed");
                            if (!result.TrySetResult(source)) source.Dispose();
                        }
                        catch { source.Dispose(); throw; }
                    }, error => result.TrySetException(error)));
                }
                catch (Exception error) { result.TrySetException(error); }
            });
            return result.Task;
        }

        internal static NetworkCombatState CaptureMechanicalStateForVerification(
            ICombatInteractionService combat, bool turnBased)
        {
            if (!turnBased) return combat.GetRealTimeCombatState();
            // Long-lived buffs restored by the native TB loader may retain their
            // associated blueprint while AbilityExecutionContext.Ability is null.
            // The combat-save digest already uses this stable projection; the
            // native before/after guard must capture the same valid world shape.
            using var projection = new CombatSaveDigestScope();
            return combat.GetRealTimeCombatState();
        }

        private static void RequireReady(ICombatInteractionService combat, string campaignId,
            string checkpointId, Func<bool> isAuthorized)
        {
            if (!RealTimePresentationRuntime.Enabled || isAuthorized == null || !isAuthorized()
                || string.IsNullOrWhiteSpace(campaignId) || !Guid.TryParseExact(checkpointId, "N", out _))
                throw new InvalidOperationException("native-checkpoint-host-barrier-not-authorized");
            var game = Game.Instance;
            string refusal = null;
            if (game?.State?.LoadedAreaState == null) refusal = "loaded-area-missing";
            else if (game.Player == null) refusal = "player-missing";
            else if (!CombatSaving.CombatSavePauseLease.IsWorldPaused(game)) refusal = "world-not-paused";
            else if (game.Player.GameId != campaignId) refusal = "campaign-mismatch";
            else if (game.Player.StartPreset?.Campaign?.DlcReward != null) refusal = "dlc-reward-world";
            else if (game.CurrentMode != GameModeType.Default && game.CurrentMode != GameModeType.Pause) refusal = "game-mode";
            else if (game.SaveManager.CurrentState != SaveManager.State.None || game.SaveManager.CommitInProgress) refusal = "save-manager-busy";
            else if (game.EntityCreator.CreationQueue.Any()) refusal = "entity-creation-pending";
            else if (game.State.LoadedAreaState.AllEntityData.Concat(game.Player.CrossSceneState.AllEntityData)
                .Any(entity => entity == null || entity.DestroyMark || entity.ShouldFadeOutViewAndDestroy)) refusal = "entity-destruction-pending";
            else if (game.State.LoadedAreaState.GetAdditionalSceneStates()
                .Any(scene => scene.IsSceneLoaded != scene.IsSceneLoadedThreadSafe)) refusal = "scene-thread-state-mismatch";
            else if (combat.IsInCrusadeTacticalCombat()) refusal = "crusade-tactical-combat";
            else if (combat.HasActiveRealTimeProjectiles()) refusal = "projectile-active";
            else if (combat.HasActiveRealTimeAtomicActions()) refusal = "atomic-action-active";
            if (refusal != null)
                throw new InvalidOperationException("native-checkpoint-host-not-at-safe-point:" + refusal);
            RealTimeNativeCharacterUiGuard.RequireIdle(game);
        }

        internal static IEnumerator FinishAfterFog(IEnumerator wait, Func<bool> authorized, Action capture, Action<Exception> fail)
        {
            var stack = new Stack<IEnumerator>();
            if (wait != null) stack.Push(wait);
            var timer = Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    object yielded = null;
                    var completed = false;
                    try
                    {
                        if (!authorized() || timer.Elapsed > TimeSpan.FromSeconds(20))
                            throw new InvalidOperationException("native-checkpoint-fog-timeout-or-obsolete-barrier");
                        if (stack.Count == 0) { capture(); completed = true; }
                        else if (!stack.Peek().MoveNext())
                        {
                            (stack.Pop() as IDisposable)?.Dispose();
                            // GPU requests are only valid during their completion frame.
                            // Do not insert a frame between native completion and capture.
                            continue;
                        }
                        else
                        {
                            yielded = stack.Peek().Current;
                            if (yielded is IEnumerator nested) { stack.Push(nested); continue; }
                        }
                    }
                    catch (Exception error)
                    {
                        fail(error); completed = true;
                        // Abandoning native GPU readback mid-enumerator would
                        // leave FogOfWarArea.m_DataRequest permanently occupied.
                        // Let it finish independently, without ever publishing a
                        // checkpoint or invoking capture after this failure.
                        while (stack.Count != 0) CoroutineRunner.Start(stack.Pop());
                    }
                    if (completed) yield break;
                    yield return yielded;
                }
            }
            finally
            {
                while (stack.Count != 0)
                {
                    try { (stack.Pop() as IDisposable)?.Dispose(); }
                    catch (Exception error) { fail(error); }
                }
            }
        }

        private static RealTimeMemorySaver CaptureNow(Game game, string checkpointId, byte[] freshFog,
            string fogScene, Func<bool> captureAuthorized, Func<bool> verify, string nativeTurn = null)
        {
            var player = game.Player;
            var area = game.State.LoadedAreaState;
            var originalArea = player.SavedInArea;
            var originalPart = player.SavedInAreaPart;
            var originalShouldLoad = area.ShouldLoad;
            var scenes = new[] { area.MainState }.Concat(area.GetAdditionalSceneStates()
                .Where(scene => scene.IsSceneLoadedThreadSafe)).Concat(new[] { player.CrossSceneState }).ToArray();
            // Avoid a second TurnOn of entities which were inactive before the
            // capture. SceneEntitiesState.TurnOn would activate all of them.
            var initiallyOn = scenes.SelectMany(scene => scene.AllEntityData).Concat(new EntityDataBase[] { area.Area, player })
                .Distinct().ToDictionary(entity => entity, entity => entity.IsTurnedOn);
            var sceneFlags = scenes.ToDictionary(scene => scene, scene => scene.IsTurnedOn);
            var sceneMembers = scenes.ToDictionary(scene => scene, scene => scene.AllEntityData.ToArray());
            if (!player.IsTurnedOn || !area.Area.IsTurnedOn || scenes.Any(scene => !scene.IsTurnedOn))
                throw new InvalidOperationException("native-checkpoint-host-lifecycle-not-ready");
            using var disabledLog = ContextData<GameLogDisabled>.Request();
            // The same native TurnOff/TurnOn lifecycle is used for RT repair
            // checkpoints and turn-based combat saves. Keep combat-start buffs,
            // derived facts and natural weapons stable for both; the explicit
            // native barrier predicate is the RT authorization in lieu of a
            // user-save pause lease.
            using var combatSaveScope = new CombatSaving.CombatSaveCaptureScope(game, captureAuthorized);
            var calculatedStats = game.State.Units.All.Where(unit => unit?.Stats?.AllStats != null)
                .ToDictionary(unit => unit, unit => RealTimeCalculatedStatReplication.Capture(unit.Stats.AllStats));
            void RestoreCalculatedStats()
            {
                foreach (var pair in calculatedStats)
                    RealTimeCalculatedStatReplication.Apply(pair.Key.Stats.AllStats, pair.Value);
            }
            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(RealTimeMemorySaver source, string file, byte[] bytes)
            {
                if (!fileNames.Add(file)) throw new InvalidDataException("native-checkpoint-scene-file-collision");
                source.SaveBytes(file, bytes);
            }
            void Json(RealTimeMemorySaver source, string file, object value)
                => Add(source, file, Serialize(value));
            void RestoreScene(SceneEntitiesState scene)
            {
                // Setter is publicized. The native wrapper has no other work
                // except this flag and entity.TurnOn, audited on game 2.7.0x.
                scene.IsTurnedOn = sceneFlags[scene];
                var errors = new List<Exception>();
                foreach (var entity in sceneMembers[scene])
                    try
                    {
                        if (initiallyOn[entity] && !entity.IsTurnedOn) entity.TurnOn();
                        else if (!initiallyOn[entity] && entity.IsTurnedOn) entity.TurnOff();
                    }
                    catch (Exception error) { errors.Add(error); }
                if (errors.Count != 0) throw new AggregateException("native-checkpoint-scene-restoration-failed", errors);
            }
            return RealTimeNativeCaptureTransaction.Capture(transaction =>
            {
                // Register first so this compensation runs last, after every
                // native TurnOn callback. Owlcat's save lifecycle can refresh a
                // stale HP/stat cache (observed as an unrelated +1 HP on an
                // off-screen enemy); neither the volatile archive nor the host
                // may publish that incidental mutation as gameplay.
                transaction.Apply(() => { }, RestoreCalculatedStats);
                transaction.Apply(() => game.SaveManager.CurrentState = SaveManager.State.Saving,
                    () => game.SaveManager.CurrentState = SaveManager.State.None);
                transaction.Apply(() =>
                {
                    player.SavedInArea = game.CurrentlyLoadedArea;
                    player.SavedInAreaPart = AreaService.Instance.CurrentAreaPart;
                    area.ShouldLoad = true;
                }, () => { player.SavedInArea = originalArea; player.SavedInAreaPart = originalPart; area.ShouldLoad = originalShouldLoad; });
                // Preserve the native area order in BOTH directions: main
                // scene, loaded additional scenes, then area. Register the
                // whole restoration before any TurnOff callback can run.
                transaction.Apply(area.TurnOff, () =>
                {
                    var errors = new List<Exception>();
                    foreach (var scene in scenes.Where(scene => scene != player.CrossSceneState))
                        try { RestoreScene(scene); } catch (Exception error) { errors.Add(error); }
                    try { if (!area.Area.IsTurnedOn) area.Area.TurnOn(); }
                    catch (Exception error) { errors.Add(error); }
                    if (errors.Count != 0) throw new AggregateException("native-checkpoint-area-restoration-failed", errors);
                });
                area.PreSave();
                transaction.Apply(player.CrossSceneState.TurnOff, () => RestoreScene(player.CrossSceneState));
                transaction.Apply(() => player.TurnOff(EntityDataBase.Reason.SaveGame), () => { if (!player.IsTurnedOn) player.TurnOn(); });
                player.PreSave();
                // The serialized off-state must contain the same caches as the
                // authoritative live world, not values recalculated by TurnOff.
                RestoreCalculatedStats();
                if (scenes.Any(scene => scene.IsTurnedOn) || initiallyOn.Keys.Any(entity => entity.IsTurnedOn)
                    || game.EntityCreator.CreationQueue.Any())
                    throw new InvalidOperationException("native-checkpoint-host-not-fully-suspended");
            }, source =>
            {
                var info = new SaveInfo
                {
                    FolderName = "RTwP-volatile-" + checkpointId + ".memory",
                    Type = SaveInfo.SaveType.Manual, Saver = source
                };
                if (nativeTurn != null) info.Name = "Multi — Combat — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                game.SaveManager.PrepareSave(info);
                if (info.Area != game.CurrentlyLoadedArea || info.DlcCampaign != null || info.SystemSaveTime == DateTime.MinValue
                    || !ReferenceEquals(info.Saver, source))
                    throw new InvalidOperationException("native-checkpoint-header-preparation-failed");
                Json(source, "header.json", info);
                Json(source, "player.json", player);
                Json(source, "party.json", player.CrossSceneState);
                foreach (var savedArea in game.State.SavedAreaStates.Concat(new[] { area }).Distinct())
                {
                    var id = savedArea.Blueprint.AssetGuidThreadSafe;
                    if (ReferenceEquals(savedArea, area))
                        Json(source, RealTimeNativeCheckpointFiles.AreaJson(id, ""), area);
                    else Add(source, RealTimeNativeCheckpointFiles.AreaJson(id, ""), ReadStash(RealTimeNativeCheckpointFiles.AreaJson(id, "")));
                    foreach (var scene in savedArea.GetAdditionalSceneStates())
                    {
                        var file = RealTimeNativeCheckpointFiles.AreaJson(id, scene.SceneName);
                        if (ReferenceEquals(savedArea, area) && scene.IsSceneLoadedThreadSafe) Json(source, file, scene);
                        else Add(source, file, ReadStash(file));
                    }
                    // Only this area's fog files; never arbitrary stash entries,
                    // its lock, or the __fog__ working files as archive entries.
                    foreach (var path in Directory.EnumerateFiles(AreaDataStash.Folder, id + ".*.fog"))
                    {
                        var file = Path.GetFileName(path);
                        Add(source, file, ReadStash(file));
                    }
                    if (ReferenceEquals(savedArea, area))
                    {
                        foreach (var key in area.SavedFogOfWarMasks.m_Masks.Keys)
                        {
                            var file = RealTimeNativeCheckpointFiles.Fog(id, key);
                            var bytes = ReadStash("__fog__" + key);
                            source.SaveBytes(file, bytes); // fresh current working mask overlays older stash
                        }
                        if (freshFog != null) source.SaveBytes(RealTimeNativeCheckpointFiles.Fog(id, fogScene), freshFog);
                    }
                }
                // This capture is RTwP-only. A native turn-based controller must
                // not be guessed or serialized as if this were a TB save.
                source.SaveJson("tbm", nativeTurn ?? "");
                source.SaveBytes("history", ReadStash("history"));
                foreach (var name in player.AdditionalTexNames)
                    Add(source, name + ".png", ReadStash(name + ".png"));
            }, () =>
            {
                if (scenes.Any(scene => scene.IsTurnedOn != sceneFlags[scene]))
                    throw new InvalidOperationException("native-checkpoint-scene-flags-changed");
                if (scenes.Any(scene => !scene.AllEntityData.SequenceEqual(sceneMembers[scene])))
                    throw new InvalidOperationException("native-checkpoint-scene-members-changed");
                if (initiallyOn.Any(pair => pair.Key.IsTurnedOn != pair.Value))
                    throw new InvalidOperationException("native-checkpoint-entity-flags-changed");
                if (player.SavedInArea != originalArea || player.SavedInAreaPart != originalPart
                    || area.ShouldLoad != originalShouldLoad || game.EntityCreator.CreationQueue.Any()
                    || game.SaveManager.CurrentState != SaveManager.State.None)
                    throw new InvalidOperationException("native-checkpoint-save-lifecycle-changed");
                combatSaveScope.VerifyWeaponsRestored();
                return verify();
            });
        }

        private static byte[] ReadStash(string file)
        {
            RealTimeMemorySaver.ValidateFileName(file);
            using var access = AreaDataStash.AccessFile(file); // flush/reopen history using native ownership
            using var stream = new FileStream(access.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return RealTimeNativeCheckpointFiles.ReadBounded(stream);
        }

        private sealed class BoundedStream : MemoryStream
        {
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Position + count > RealTimeMemorySaver.MaximumFileBytes)
                    throw new InvalidDataException("native-checkpoint-size-limit");
                base.Write(buffer, offset, count);
            }
            public override void WriteByte(byte value)
            {
                if (Position >= RealTimeMemorySaver.MaximumFileBytes) throw new InvalidDataException("native-checkpoint-size-limit");
                base.WriteByte(value);
            }
        }

        private static byte[] Serialize(object value)
        {
            using var bytes = new BoundedStream();
            using (var text = new StreamWriter(bytes, new UTF8Encoding(false, true), 4096, true))
            using (var writer = new JsonTextWriter(text))
                JsonSerializer.Create(DefaultJsonSettings.DefaultSettings).Serialize(writer, value);
            return bytes.ToArray();
        }
    }
}
