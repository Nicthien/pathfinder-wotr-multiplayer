using System;
using System.Threading.Tasks;
using Kingmaker.EntitySystem.Persistence;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Localization;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerClient
    {
        private readonly object _transitionPositionLock = new();
        private NetworkGame _transitionPositionSession;
        private long _latestTransitionPositionSequence;

        public void RequestPartyRegroup(long targetPlayerId)
        {
            var session = Game;
            if (!IsActive || session?.CurrentArea == null) return;
            Send(new ClientPartyRegroupRequested { TargetPlayerId = targetPlayerId, SessionId = session.Id,
                LoadSeed = session.LoadedSaveSeed, AreaSeed = session.CurrentArea.Seed });
        }

        private void OnNotifyPartyRegrouped(long sender, NotifyPartyRegrouped message)
        {
            var session = Game;
            if (sender != NetworkConstants.HostPlayerId || session == null || message == null) return;
            if (message.IsAreaTransition)
            {
                if (!message.Success || message.Sequence <= 0 || string.IsNullOrWhiteSpace(message.AreaPartId)
                    || !string.Equals(session.Id, message.SessionId, StringComparison.Ordinal)
                    || session.LoadedSaveSeed != message.LoadSeed || session.CurrentArea?.Seed != message.AreaSeed) return;
                lock (_transitionPositionLock)
                {
                    if (!ReferenceEquals(_transitionPositionSession, session))
                    {
                        _transitionPositionSession = session;
                        _latestTransitionPositionSequence = 0;
                    }
                    if (message.Sequence <= _latestTransitionPositionSequence) return;
                    _latestTransitionPositionSequence = message.Sequence;
                }
                SafeFireAndForget(() => ApplyTransitionPositionsAsync(session, message));
                return;
            }
            MainThreadAccessor.Post(() =>
            {
                if (!ReferenceEquals(Game, session) || session.CurrentArea == null
                    || !string.Equals(session.Id, message.SessionId, StringComparison.Ordinal)
                    || session.LoadedSaveSeed != message.LoadSeed || session.CurrentArea.Seed != message.AreaSeed) return;
                if (!message.Success)
                {
                    if (message.RequesterId == session.LocalPlayerId)
                        PlayerNotification.ShowWarningNotification(WellKnownKeys.LobbyWindow.RealTime.RegroupUnavailable.Key);
                    return;
                }
                if (!CombatInteraction.ApplyPartyRegroup(message.Positions))
                {
                    Logger.LogError("Unable to apply host party regroup; local party positions may differ.");
                    PlayerNotification.ShowWarningNotification(WellKnownKeys.LobbyWindow.RealTime.RegroupUnavailable.Key);
                }
            });
        }

        private async Task ApplyTransitionPositionsAsync(NetworkGame session, NotifyPartyRegrouped message)
        {
            for (var attempt = 0; attempt < 360; attempt++)
            {
                await Task.Delay(250);
                var check = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                MainThreadAccessor.Post(() =>
                {
                    try
                    {
                        lock (_transitionPositionLock)
                            if (!ReferenceEquals(_transitionPositionSession, session)
                                || _latestTransitionPositionSequence != message.Sequence)
                                { check.TrySetResult(-1); return; }
                        if (!IsActive || !ReferenceEquals(Game, session) || session.LoadedSaveSeed != message.LoadSeed
                            || session.CurrentArea?.Seed != message.AreaSeed) { check.TrySetResult(-1); return; }
                        if (LoadingProcess.Instance.IsLoadingInProcess
                            || !string.Equals(Kingmaker.Game.Instance.CurrentlyLoadedAreaPart?.AssetGuid.ToString(),
                                message.AreaPartId, StringComparison.OrdinalIgnoreCase)) { check.TrySetResult(0); return; }
                        if (!CombatInteraction.ApplyPartyRegroup(message.Positions))
                        {
                            Logger.LogError("Unable to apply host positions after an area transition.");
                            PlayerNotification.ShowWarningNotification(WellKnownKeys.LobbyWindow.RealTime.TransitionSyncFailed.Key);
                        }
                        else Logger.LogInformation("Applied host transition positions. AreaPart={AreaPart}, Units={Units}",
                            message.AreaPartId, message.Positions.Count);
                        check.TrySetResult(1);
                    }
                    catch (Exception error) { check.TrySetException(error); }
                });
                var result = await check.Task;
                if (result != 0) return;
            }
            Logger.LogWarning("Area transition position application timed out. AreaPart={AreaPart}", message.AreaPartId);
            PlayerNotification.ShowWarningNotification(WellKnownKeys.LobbyWindow.RealTime.TransitionSyncFailed.Key);
        }
    }
}
