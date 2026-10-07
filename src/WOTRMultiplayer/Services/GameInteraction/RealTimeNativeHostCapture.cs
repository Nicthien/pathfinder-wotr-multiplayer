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
        {
            var result = new TaskCompletionSource<RealTimeMemorySaver>(TaskCreationOptions.RunContinuationsAsynchronously);
            mainThread.Post(() =>
            {
                try
                {
                    RequireReady(combat, campaignId, checkpointId, isAuthorized);
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
                        && game.TimeController.GameTime == time && game.IsPaused;
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
                        var before = combat.GetRealTimeCombatState();
                        var beforeHash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = before });
                        RealTimeMemorySaver source;
                        try { source = CaptureNow(game, checkpointId, freshFog, fogScene, () =>
                        {
                            if (!SameWorld()) return false;
                            var after = combat.GetRealTimeCombatState();
                            return RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = after }) == beforeHash
                                && RealTimeCombatTemporalDiff.Summarize(before, after) == null;
                        }); }
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

        private static void RequireReady(ICombatInteractionService combat, string campaignId,
            string checkpointId, Func<bool> isAuthorized)
        {
            if (!RealTimePresentationRuntime.Enabled || isAuthorized == null || !isAuthorized()
                || string.IsNullOrWhiteSpace(campaignId) || !Guid.TryParseExact(checkpointId, "N", out _))
                throw new InvalidOperationException("native-checkpoint-host-barrier-not-authorized");
            var game = Game.Instance;
            if (game?.State?.LoadedAreaState == null || game.Player == null || !game.IsPaused
                || game.Player.GameId != campaignId || game.Player.StartPreset?.Campaign?.DlcReward != null
                || game.CurrentMode != GameModeType.Default && game.CurrentMode != GameModeType.Pause
                || game.SaveManager.CurrentState != SaveManager.State.None || game.SaveManager.CommitInProgress
                || game.EntityCreator.CreationQueue.Any()
                || game.State.LoadedAreaState.AllEntityData.Concat(game.Player.CrossSceneState.AllEntityData)
                    .Any(entity => entity == null || entity.DestroyMark || entity.ShouldFadeOutViewAndDestroy)
                || game.State.LoadedAreaState.GetAdditionalSceneStates()
                    .Any(scene => scene.IsSceneLoaded != scene.IsSceneLoadedThreadSafe)
                || combat.IsInCrusadeTacticalCombat() || combat.HasActiveRealTimeProjectiles()
                || combat.HasActiveRealTimeAtomicActions())
                throw new InvalidOperationException("native-checkpoint-host-not-at-safe-point");
        }

        private static IEnumerator FinishAfterFog(IEnumerator wait, Func<bool> authorized, Action capture, Action<Exception> fail)
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
                        else if (!stack.Peek().MoveNext()) (stack.Pop() as IDisposable)?.Dispose();
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
            string fogScene, Func<bool> verify)
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
                source.SaveJson("tbm", "");
                source.SaveBytes("history", ReadStash("history"));
                foreach (var name in player.AdditionalTexNames)
                    Add(source, name + ".png", ReadStash(name + ".png"));
            }, () =>
            {
                return scenes.All(scene => scene.IsTurnedOn == sceneFlags[scene]
                    && scene.AllEntityData.SequenceEqual(sceneMembers[scene]))
                    && initiallyOn.All(pair => pair.Key.IsTurnedOn == pair.Value)
                    && player.SavedInArea == originalArea && player.SavedInAreaPart == originalPart
                    && area.ShouldLoad == originalShouldLoad && !game.EntityCreator.CreationQueue.Any()
                    && game.SaveManager.CurrentState == SaveManager.State.None && verify();
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
