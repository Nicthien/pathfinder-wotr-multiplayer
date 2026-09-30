using System;
using WOTRMultiplayer.Entities.Combat.RealTime;
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
                    return !string.IsNullOrWhiteSpace(unitId) && IsFinite(command.Movement.Destination.X)
                        && IsFinite(command.Movement.Destination.Y) && IsFinite(command.Movement.Destination.Z);
                case RealTimeCommandKind.Attack when command.Attack != null:
                    unitId = command.Attack.InitiatorUnitId;
                    return !string.IsNullOrWhiteSpace(unitId) && !string.IsNullOrWhiteSpace(command.Attack.TargetUnitId);
                case RealTimeCommandKind.UseAbility when command.Ability?.Ability != null && command.Ability.Target != null:
                    unitId = command.Ability.InitiatorUnitId;
                    return !string.IsNullOrWhiteSpace(unitId);
                default:
                    return false;
            }
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
