using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Kingmaker;
using Kingmaker.EntitySystem.Persistence;
using Kingmaker.GameModes;
using Kingmaker.Utility;
using WOTRMultiplayer.Abstractions.Unity;
using WOTRMultiplayer.Abstractions.GameInteraction;

namespace WOTRMultiplayer.Services.GameInteraction
{
    internal sealed class RealTimeNativeWorldLoadResult
    {
        internal object World { get; set; }
        internal object RepairObligation { get; set; }
    }

    // Runtime entry only. Not called by offline tests/probes or by the old
    // repair coordinator. Returns a LOADED world, never a verified Commit.
    internal static class RealTimeNativeClientLoad
    {
        internal static Task<RealTimeNativeWorldLoadResult> LoadAsync(IMainThreadAccessor mainThread,
            object owner, ICombatInteractionService combat, RealTimeMemorySaver source, string campaignId, string checkpointId, Func<bool> authorized)
        {
            var completion = new TaskCompletionSource<RealTimeNativeWorldLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            mainThread.Post(() =>
            {
                RealTimeNativeLoadLease lease = null;
                SaveInfo info = null;
                try
                {
                    if (!RealTimePresentationRuntime.Enabled || authorized == null || !authorized()
                        || !RealTimePresentationRuntime.IsClientActive || RealTimeNativeLoadLease.IsActive)
                        throw new InvalidOperationException("native-load-client-barrier-not-authorized");
                    var game = Game.Instance;
                    if (game?.State?.LoadedAreaState == null || game.Player == null || !game.IsPaused
                        || game.Player.GameId != campaignId || game.Player.StartPreset?.Campaign?.DlcReward != null
                        || game.CurrentMode != GameModeType.Default && game.CurrentMode != GameModeType.Pause
                        || game.SaveManager.CurrentState != SaveManager.State.None || game.SaveManager.CommitInProgress
                        || LoadingProcess.Instance.IsLoadingInProcess)
                        throw new InvalidOperationException("native-load-client-not-at-safe-point");
                    // All JSON syntax/header checks happen before LoadArea can
                    // schedule world disposal or native ClearDirectory.
                    info = RealTimeNativeCheckpointLoadSource.Prepare(source, campaignId, checkpointId);
                    if (info.Type != SaveInfo.SaveType.Manual || info.Area == null || info.Area != game.CurrentlyLoadedArea
                        || info.DlcCampaign != null || info.Campaign?.DlcReward != null
                        || source.ReadBytes(info.Area.AssetGuidThreadSafe + ".json") == null)
                        throw new InvalidDataException("native-load-header-area-or-campaign-unsupported");
                    var root = RealTimeNativeStashPaths.ValidateRoot(AreaDataStash.Folder,
                        Kingmaker.Utility.ApplicationPaths.persistentDataPath, Process.GetCurrentProcess().Id);
                    var previousPlayer = game.Player;
                    bool Authorized() => authorized() && ReferenceEquals(Game.Instance, game);
                    lease = RealTimeNativeLoadLease.Begin(owner, info, Authorized, (name, bytes) =>
                    {
                        if (!Authorized() || !string.Equals(AreaDataStash.Folder, root, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("native-load-stash-owner-changed");
                        var target = RealTimeNativeStashPaths.Target(root, name);
                        using var access = AreaDataStash.AccessFile(name);
                        if (!string.Equals(Path.GetFullPath(access.Path), target, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("native-load-stash-accessor-escaped");
                        // Native history accessor closes/reopens its log around
                        // this write. No player save or arbitrary path is opened.
                        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                        output.Write(bytes, 0, bytes.Length);
                    });
                    RealTimeNativeGraphRepair.Add(game.State, lease);
                    combat.ClearRealTimePresentationForNativeLoad();
                    // Preserve the native LoadGame sound teardown even though its
                    // multiplayer ForceLoadGame hook must not reset our session.
                    Kingmaker.Visual.Sound.SoundState.Instance.ResetBeforeUnloading();
                    // Direct LoadArea avoids ForceLoadGame/TransferSaveGame and
                    // their session reset. Its exact SaveInfo is admitted once.
                    game.LoadArea(info.Area, null, AutoSaveMode.None, false, info, lease.MarkNativeCallback);
                    CoroutineRunner.Start(WaitForLoad(game, previousPlayer, lease, campaignId, completion));
                }
                catch (Exception error)
                {
                    if (lease != null)
                    {
                        lease.Fail(error.Message);
                        // A native scheduling exception may leave work queued.
                        // Keep ownership until that work drains, never revoke
                        // the stash writer underneath native worker threads.
                        CoroutineRunner.Start(WaitForLoad(Game.Instance, null, lease, campaignId, completion));
                    }
                    else info?.Saver?.Dispose();
                    completion.TrySetException(error);
                }
            });
            return completion.Task;
        }

        private static IEnumerator WaitForLoad(Game game, Player previousPlayer, RealTimeNativeLoadLease lease,
            string campaignId, TaskCompletionSource<RealTimeNativeWorldLoadResult> completion)
        {
            var timer = Stopwatch.StartNew();
            var drained = false;
            try
            {
                while (true)
                {
                    var pending = true;
                    var observed = false;
                    try
                    {
                        if (game?.State != null) RealTimeNativeGraphRepair.Add(game.State, lease);
                        if (game != null) game.IsPaused = true;
                        if (!lease.IsAuthorized) lease.Fail("native-load-context-revoked");
                        pending = LoadingProcess.Instance.IsLoadingInProcess;
                        observed = true;
                        if (timer.Elapsed > TimeSpan.FromMinutes(2)) lease.Fail("native-load-deadline-exceeded");
                        if (lease.Fault != null) completion.TrySetException(new InvalidOperationException(lease.Fault));
                        if (!pending)
                        {
                            drained = true;
                            if (!lease.NativeCallbackCompleted || lease.Fault != null || game?.Player?.GameId != campaignId
                                || previousPlayer == null || ReferenceEquals(game.Player, previousPlayer)
                                || game.State?.LoadedAreaState == null || game.CurrentlyLoadedArea != lease.Info.Area
                                || game.SaveManager.CurrentState != SaveManager.State.None || !game.IsPaused)
                                throw new InvalidOperationException(lease.Fault ?? "native-load-world-not-confirmed");
                            completion.TrySetResult(new RealTimeNativeWorldLoadResult { World = game.State, RepairObligation = lease });
                            break;
                        }
                    }
                    catch (Exception error)
                    {
                        lease.Fail(error.Message); completion.TrySetException(error);
                        // If the native loading handle is still live, keep
                        // watching it; do not assume an observation error means
                        // worker completion or start another native load.
                        if (observed && !pending) { drained = true; break; }
                    }
                    yield return null;
                }
            }
            finally
            {
                // Mechanical obligation remains on old/new worlds. Only a
                // future verified barrier may remove it and resume/save.
                if (game?.State != null) RealTimeNativeGraphRepair.Add(game.State, lease);
                if (drained) lease.Dispose();
                else CoroutineRunner.Start(WaitForLoad(game, null, lease, campaignId, completion));
            }
        }
    }
}
