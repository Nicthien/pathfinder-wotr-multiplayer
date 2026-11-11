using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Area;
using Kingmaker.EntitySystem.Persistence;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Entities.Area;
using WOTRMultiplayer.Localization;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerHost
    {
        private long _transitionPositionSequence;

        private void ScheduleTransitionPositions(NetworkAreaTransition transition)
        {
            var session = Game;
            var target = ResourcesLibrary.TryGetBlueprint<BlueprintAreaEnterPoint>(transition.To?.Id);
            if (session?.CurrentArea == null || target?.Area == null
                || !string.Equals(target.Area.AssetGuid.ToString(), session.CurrentArea.Id, StringComparison.OrdinalIgnoreCase)) return;
            var partId = (target.AreaPart ?? target.Area).AssetGuid.ToString();
            var sequence = Interlocked.Increment(ref _transitionPositionSequence);
            var saveSeed = session.LoadedSaveSeed;
            var areaSeed = session.CurrentArea.Seed;
            Logger.LogInformation("Scheduled transition position confirmation. TargetPart={TargetPart}, Sequence={Sequence}", partId, sequence);
            SafeFireAndForget(() => BroadcastTransitionPositionsAsync(session, saveSeed, areaSeed, partId, sequence));
        }

        private async Task BroadcastTransitionPositionsAsync(Entities.NetworkGame session, int saveSeed,
            int areaSeed, string partId, long sequence)
        {
            for (var attempt = 0; attempt < 360; attempt++)
            {
                await Task.Delay(250);
                var check = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                MainThreadAccessor.Post(() =>
                {
                    try
                    {
                        if (!IsActive || !ReferenceEquals(Game, session) || session.LoadedSaveSeed != saveSeed
                            || session.CurrentArea?.Seed != areaSeed || Interlocked.Read(ref _transitionPositionSequence) != sequence)
                            { check.TrySetResult(-1); return; }
                        var loading = LoadingProcess.Instance.IsLoadingInProcess;
                        var currentPart = Kingmaker.Game.Instance.CurrentlyLoadedAreaPart?.AssetGuid.ToString();
                        if (loading || !string.Equals(currentPart, partId, StringComparison.OrdinalIgnoreCase))
                        {
                            if (attempt % 40 == 0) Logger.LogInformation("Waiting to confirm transition positions. Loading={Loading}, CurrentPart={CurrentPart}, TargetPart={TargetPart}", loading, currentPart, partId);
                            check.TrySetResult(0); return;
                        }
                        var positions = CombatInteraction.CapturePartyRegroupPositions();
                        if (positions.Count == 0) { check.TrySetResult(0); return; }
                        Send(new NotifyPartyRegrouped { SessionId = session.Id, LoadSeed = saveSeed, AreaSeed = areaSeed,
                            Success = true, IsAreaTransition = true, AreaPartId = partId,
                            Sequence = sequence, Positions = positions });
                        Logger.LogInformation("Confirmed transition positions sent. AreaPart={AreaPart}, Units={Units}", partId, positions.Count);
                        check.TrySetResult(1);
                    }
                    catch (Exception error) { check.TrySetException(error); }
                });
                var result = await check.Task;
                if (result != 0) return;
            }
            Logger.LogWarning("Area transition position broadcast timed out. AreaPart={AreaPart}", partId);
        }

        public void RequestPartyRegroup(long targetPlayerId)
        {
            var session = Game;
            if (!IsActive || session?.CurrentArea == null) return;
            QueuePartyRegroup(session.LocalPlayerId, new ClientPartyRegroupRequested
            {
                TargetPlayerId = targetPlayerId, SessionId = session.Id,
                LoadSeed = session.LoadedSaveSeed, AreaSeed = session.CurrentArea.Seed
            });
        }

        private void OnClientPartyRegroupRequested(long playerId, ClientPartyRegroupRequested message)
            => QueuePartyRegroup(playerId, message);

        private void QueuePartyRegroup(long playerId, ClientPartyRegroupRequested message)
        {
            var session = Game;
            MainThreadAccessor.Post(() =>
            {
                if (!ReferenceEquals(Game, session) || session == null || message == null) return;
                var reply = new NotifyPartyRegrouped { SessionId = session.Id, LoadSeed = session.LoadedSaveSeed,
                    AreaSeed = session.CurrentArea?.Seed ?? 0, RequesterId = playerId };
                try
                {
                    if (IsActive && session.CurrentArea != null && !session.CurrentArea.IsGlobalMap
                        && session.Combat == null && session.ForcedPause == null && !IsSaveLoadPending
                        && string.Equals(session.Id, message.SessionId, StringComparison.Ordinal)
                        && session.LoadedSaveSeed == message.LoadSeed && session.CurrentArea.Seed == message.AreaSeed
                        && playerId != message.TargetPlayerId && GetPlayer(playerId) != null
                        && GetPlayer(message.TargetPlayerId) != null)
                    {
                        var own = session.Characters.Where(c => c.Owner?.Id == playerId && !string.IsNullOrEmpty(c.UnitId))
                            .Select(c => c.UnitId).ToList();
                        var target = session.Characters.Where(c => c.Owner?.Id == message.TargetPlayerId && !string.IsNullOrEmpty(c.UnitId))
                            .Select(c => c.UnitId).ToList();
                        reply.Success = CombatInteraction.TryRegroupParty(own, target, out var positions);
                        if (reply.Success) reply.Positions = positions;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Party regroup failed. PlayerId={PlayerId}", playerId);
                }
                if (reply.Success) Send(reply);
                else if (playerId == session.LocalPlayerId)
                    PlayerNotification.ShowWarningNotification(WellKnownKeys.LobbyWindow.RealTime.RegroupUnavailable.Key);
                else if (GetPlayer(playerId) != null) Send(playerId, reply);
            });
        }
    }
}
