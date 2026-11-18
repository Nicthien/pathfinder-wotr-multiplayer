using System;
using System.Linq;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerHost
    {
        private const int FeatureControlTransferTimeoutSeconds = 10;

        public override void RequestFeatureControl(NetworkPlayerControlledFeature feature)
        {
            if (!IsActive || Game == null || HasControlOverFeature(feature)) return;
            RequestFeatureControl(Game.LocalPlayerId, feature, Game.Id, DateTime.UtcNow.Ticks);
        }

        internal void TickFeatureControlTransfers()
        {
            if (!IsActive || Game == null) return;

            foreach (var pending in Game.FeatureControlTransfers.ToArray())
            {
                var transfer = pending.Value;
                if (GetSyncedPlayers().All(player => player.Id != transfer.RequesterId))
                {
                    RejectFeatureControlTransfer(pending.Key, transfer, "requester-disconnected");
                    continue;
                }

                if (DateTime.UtcNow.Ticks - transfer.CreatedUtcTicks > TimeSpan.FromSeconds(FeatureControlTransferTimeoutSeconds).Ticks)
                {
                    RejectFeatureControlTransfer(pending.Key, transfer, "timeout");
                    continue;
                }

                if (IsFeatureControlTransferSafe(pending.Key))
                {
                    AcceptFeatureControlTransfer(pending.Key, transfer);
                }
            }
        }

        private void OnClientFeatureControlRequested(long receivedFrom, ClientFeatureControlRequested message)
        {
            if (message == null || !Enum.TryParse(message.Feature, out NetworkPlayerControlledFeature feature)) return;
            _mainThreadAccessor.Post(() => RequestFeatureControl(receivedFrom, feature, message.SessionId, message.Sequence));
        }

        private void RequestFeatureControl(long requesterId, NetworkPlayerControlledFeature feature, string sessionId, long sequence)
        {
            if (Game == null || !string.Equals(Game.Id, sessionId, StringComparison.Ordinal) || sequence <= 0
                || !IsSharedFeature(feature) || GetSyncedPlayers().All(player => player.Id != requesterId))
            {
                Send(new NotifyFeatureControlRequestResolved { Feature = feature.ToString(), RequesterId = requesterId, Sequence = sequence, Accepted = false, Reason = "invalid-request", SessionId = Game?.Id });
                return;
            }

            if (HasControlOverPlayer(feature, requesterId))
            {
                Send(new NotifyFeatureControlRequestResolved { Feature = feature.ToString(), RequesterId = requesterId, Sequence = sequence, Accepted = false, Reason = "already-controller", SessionId = Game.Id });
                return;
            }

            if (Game.FeatureControlTransfers.ContainsKey(feature))
            {
                Send(new NotifyFeatureControlRequestResolved { Feature = feature.ToString(), RequesterId = requesterId, Sequence = sequence, Accepted = false, Reason = "request-pending", SessionId = Game.Id });
                return;
            }

            var transfer = new NetworkFeatureControlTransfer
            {
                RequesterId = requesterId,
                Sequence = sequence,
                SessionId = sessionId,
                CreatedUtcTicks = DateTime.UtcNow.Ticks
            };
            Game.FeatureControlTransfers.TryAdd(feature, transfer);
            SendAdvancedControlsUpdate();
            TickFeatureControlTransfers();
        }

        private bool IsFeatureControlTransferSafe(NetworkPlayerControlledFeature feature)
        {
            switch (feature)
            {
                case NetworkPlayerControlledFeature.Dialogs:
                    var dialog = Game.DialogState;
                    return dialog == null || (!dialog.IsSelectingAnswer && !dialog.VoteCommitted && dialog.Answer == null);
                case NetworkPlayerControlledFeature.CrusadeArmyCombat:
                    var turn = Game.ArmyCombat?.Turn;
                    return turn == null || (!turn.IsSyncRequired && turn.SyncedPlayers.Count >= GetSyncedPlayersCount());
                case NetworkPlayerControlledFeature.VendorDeal:
                    // Vendor operations arrive over the ordered host connection.
                    // The host validates each operation before it relays it, so an
                    // earlier operation is settled before this request is handled.
                    return true;
                default:
                    return false;
            }
        }

        private void AcceptFeatureControlTransfer(NetworkPlayerControlledFeature feature, NetworkFeatureControlTransfer transfer)
        {
            if (!Game.FeatureControlTransfers.TryRemove(feature, out _)) return;
            UpdateFeatureControl(feature, transfer.RequesterId);
            SendAdvancedControlsUpdate();
            Send(new NotifyFeatureControlRequestResolved { Feature = feature.ToString(), RequesterId = transfer.RequesterId, Sequence = transfer.Sequence, Accepted = true, SessionId = transfer.SessionId });
        }

        private void RejectFeatureControlTransfer(NetworkPlayerControlledFeature feature, NetworkFeatureControlTransfer transfer, string reason)
        {
            if (!Game.FeatureControlTransfers.TryRemove(feature, out _)) return;
            SendAdvancedControlsUpdate();
            Send(new NotifyFeatureControlRequestResolved { Feature = feature.ToString(), RequesterId = transfer.RequesterId, Sequence = transfer.Sequence, Accepted = false, Reason = reason, SessionId = transfer.SessionId });
        }

        private static bool IsSharedFeature(NetworkPlayerControlledFeature feature)
            => feature == NetworkPlayerControlledFeature.Dialogs
                || feature == NetworkPlayerControlledFeature.VendorDeal
                || feature == NetworkPlayerControlledFeature.CrusadeArmyCombat;

        private bool HasControlOverPlayer(NetworkPlayerControlledFeature feature, long playerId)
            => Game.FeatureControllers.TryGetValue(feature, out var controller) && controller == playerId;

        internal bool IsVendorOperationAuthorized(long playerId)
            => Game?.FeatureControllers.TryGetValue(NetworkPlayerControlledFeature.VendorDeal, out var controller) == true
                && controller == playerId && !Game.FeatureControlTransfers.ContainsKey(NetworkPlayerControlledFeature.VendorDeal);
    }
}
