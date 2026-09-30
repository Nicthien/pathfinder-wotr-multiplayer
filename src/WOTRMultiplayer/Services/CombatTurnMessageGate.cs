using System;

namespace WOTRMultiplayer.Services
{
    public static class CombatTurnMessageGate
    {
        public static bool IsCurrentEndMessage(string currentUnitId, string messageUnitId, long receivedFrom, long declaredPlayerId, bool validateSender)
        {
            return !string.IsNullOrWhiteSpace(currentUnitId)
                && string.Equals(currentUnitId, messageUnitId, StringComparison.OrdinalIgnoreCase)
                && (!validateSender || receivedFrom == declaredPlayerId);
        }
    }
}
