using System;
using System.Collections.Generic;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking.Messages.Contracts;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Services
{
    public static class RealTimeCombatCommandValidator
    {
        public static bool TryGetUnitId(ClientRealTimeCombatCommand command, out string unitId)
        {
            unitId = null;
            if (command == null)
            {
                return false;
            }

            var payloadCount = (command.Movement != null ? 1 : 0)
                + (command.Attack != null ? 1 : 0)
                + (command.Ability != null ? 1 : 0);
            if (payloadCount != 1)
            {
                return false;
            }

            switch ((RealTimeCommandKind)command.Kind)
            {
                case RealTimeCommandKind.Move when command.Movement?.Destination != null:
                    unitId = command.Movement.InitiatorUnitId;
                    return !string.IsNullOrWhiteSpace(unitId)
                        && IsFiniteVector(command.Movement.Destination)
                        && (command.Movement.Position == null || IsFiniteVector(command.Movement.Position))
                        && IsFinitePath(command.Movement.VectorPath)
                        && IsFinite(command.Movement.MovementDelay)
                        && (!command.Movement.Orientation.HasValue || IsFinite(command.Movement.Orientation.Value))
                        && (!command.Movement.SpeedLimit.HasValue || IsFinite(command.Movement.SpeedLimit.Value));
                case RealTimeCommandKind.Attack when command.Attack != null:
                    unitId = command.Attack.InitiatorUnitId;
                    return !string.IsNullOrWhiteSpace(unitId)
                        && !string.IsNullOrWhiteSpace(command.Attack.TargetUnitId)
                        && IsFinitePath(command.Attack.VectorPath);
                case RealTimeCommandKind.UseAbility when command.Ability?.Ability != null && command.Ability.Target != null:
                    unitId = command.Ability.InitiatorUnitId;
                    return !string.IsNullOrWhiteSpace(unitId)
                        && (!string.IsNullOrWhiteSpace(command.Ability.Ability.Id)
                            || !string.IsNullOrWhiteSpace(command.Ability.Ability.BlueprintId))
                        && (command.Ability.Target.Point != null || !string.IsNullOrWhiteSpace(command.Ability.Target.UnitId))
                        && (command.Ability.Target.Point == null || IsFiniteVector(command.Ability.Target.Point))
                        && (!command.Ability.Target.Orientation.HasValue || IsFinite(command.Ability.Target.Orientation.Value))
                        && IsFinitePath(command.Ability.VectorPath);
                default:
                    return false;
            }
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFiniteVector(NetworkVector3 value) => value != null
            && IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);

        private static bool IsFinitePath(IReadOnlyCollection<NetworkVector3> path)
        {
            if (path == null)
            {
                return true;
            }

            if (path.Count > 4096)
            {
                return false;
            }

            foreach (var point in path)
            {
                if (!IsFiniteVector(point))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
