using System;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using ProtoBuf;
using TurnBased.Controllers;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.Services
{
    public abstract partial class MultiplayerActorBase
    {
        private long _movementOrdinal, _movementPublished, _movementSequence, _movementApplied;
        private string _movementScope;
        private NotifyTurnMovementState _pendingMovement;
        private long _movementSettled;
        private protected bool HasPendingTurnMovement => _movementOrdinal >
            (this is MultiplayerHost ? _movementPublished : _movementSettled);
        private readonly System.Collections.Concurrent.ConcurrentQueue<NotifyTurnMovementState> _movementInbox = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<NetworkUnitMoveTo> _movementStarts = new();
        private bool IsCurrentMovement(NotifyTurnMovementState frame) => frame != null
            && frame.CombatSeed == Game.Combat.Seed && frame.Epoch == Game.RealTimeCombat.Epoch
            && TurnMovementState.IsCurrent(frame, Game.Id, Game.SessionSeed, Game.LoadedSaveSeed,
                Game.Combat.Turn.Seed.Value, Game.Combat.Turn.UnitId, _movementOrdinal, _movementApplied);

        private bool PrepareMovementScope()
        {
            if (!CombatSaveActivation.Enabled || !(this is MultiplayerHost host && host.IsActive
                || this is MultiplayerClient client && client.IsActive) || Game?.Combat?.Turn?.Seed == null
                || Game.Combat.Turn.IsAI || Game.RealTimeCombat.Mode != Entities.Combat.RealTime.CombatMode.TurnBased)
                return false;
            var scope = Game.Id + ":" + Game.SessionSeed + ":" + Game.LoadedSaveSeed + ":"
                + Game.Combat.Seed + ":" + Game.RealTimeCombat.Epoch + ":" + Game.Combat.Turn.Seed + ":" + Game.Combat.Turn.UnitId;
            if (scope != _movementScope)
            {
                _movementScope = scope;
                _movementOrdinal = _movementPublished = _movementSequence = _movementApplied = _movementSettled = 0;
                _pendingMovement = null;
            }
            return true;
        }

        private void StampTurnMovement(NetworkUnitMoveTo move)
        {
            if (!PrepareMovementScope() || this is not MultiplayerHost || !Game.Combat.Turn.IsLocalPlayer
                || move.InitiatorUnitId != Game.Combat.Turn.UnitId) return;
            move.TurnMovement = ++_movementOrdinal;
        }

        private void ObserveTurnMovement(long peer, NetworkUnitMoveTo move)
        {
            if (peer == NetworkConstants.HostPlayerId && this is MultiplayerClient
                && move.TurnMovement > 0 && _movementStarts.Count < 16) _movementStarts.Enqueue(move);
        }

        private void OnTurnMovementState(long peer, NotifyTurnMovementState message)
        {
            if (peer != NetworkConstants.HostPlayerId || this is not MultiplayerClient || message?.State == null
                || message.State.Length > 256 * 1024) return;
            var frame = Serializer.DeepClone(message);
            if (_movementInbox.Count < 16) _movementInbox.Enqueue(frame);
        }

        internal void TickTurnMovement()
        {
            if (!PrepareMovementScope())
            { while (_movementInbox.TryDequeue(out _)) { } while (_movementStarts.TryDequeue(out _)) { } return; }
            while (_movementStarts.TryDequeue(out var started))
                if (!Game.Combat.Turn.IsLocalPlayer && started.InitiatorUnitId == Game.Combat.Turn.UnitId)
                {
                    _movementOrdinal = Math.Max(_movementOrdinal, started.TurnMovement);
                    if (_pendingMovement?.Movement < _movementOrdinal) _pendingMovement = null;
                }
            while (_movementInbox.TryDequeue(out var received))
                if (!Game.Combat.Turn.IsLocalPlayer && IsCurrentMovement(received)
                    && (_pendingMovement == null || received.Sequence > _pendingMovement.Sequence))
                    _pendingMovement = received;
            if (CombatSavePauseLease.Active || CombatSaveLoadState.Active) return;
            var native = Kingmaker.Game.Instance;
            var turn = native?.TurnBasedCombatController?.CurrentTurn;
            if (turn?.Rider?.UniqueId != Game.Combat.Turn.UnitId || turn.HasRunningCommands
                || !TurnBasedSaveNativeState.IsStableStatus(turn.Status)
                || TurnBasedSaveNativeState.HasPendingAction(turn.Rider)
                || TurnBasedSaveNativeState.HasPendingAction(turn.Mount)) return;
            try
            {
                if (this is MultiplayerHost && Game.Combat.Turn.IsLocalPlayer
                    && _movementOrdinal > _movementPublished)
                {
                    var state = TurnMovementState.Capture(turn);
                    Send(new NotifyTurnMovementState { Session = Game.Id, SessionSeed = Game.SessionSeed,
                        LoadSeed = Game.LoadedSaveSeed, TurnSeed = Game.Combat.Turn.Seed.Value,
                        Unit = turn.Rider.UniqueId, Movement = _movementOrdinal, Sequence = ++_movementSequence,
                        CombatSeed = Game.Combat.Seed, Epoch = Game.RealTimeCombat.Epoch,
                        State = state });
                    _movementPublished = _movementOrdinal;
                }
                else if (this is MultiplayerClient && _pendingMovement != null)
                {
                    var frame = _pendingMovement;
                    if (!IsCurrentMovement(frame))
                    { _pendingMovement = null; return; }
                    TurnMovementState.Apply(turn, frame.State);
                    _movementApplied = frame.Sequence;
                    _movementSettled = frame.Movement;
                    _pendingMovement = null;
                }
            }
            catch (Exception error)
            {
                Logger.LogError(error, "[CombatSave] Movement state synchronization failed; world held for recovery.");
                CombatSaveLoadState.Owner = this;
                CombatSaveLoadState.BindLoadedWorld(native.State);
                CombatSavePauseLease.FreezeClock(native);
                _pendingMovement = null;
                _movementPublished = _movementOrdinal;
            }
        }
    }
}
