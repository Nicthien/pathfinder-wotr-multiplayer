using System;
using WOTRMultiplayer.Entities.Combat;

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

        public static bool IsCurrentEndSynchronization(NetworkCombatTurnStage stage, string currentUnitId,
            string messageUnitId, long receivedFrom, long declaredPlayerId,
            long messageSequence, long currentSequence) =>
            stage == NetworkCombatTurnStage.EndSynchronization
            && messageSequence > 0 && messageSequence == currentSequence
            && IsCurrentEndMessage(currentUnitId, messageUnitId, receivedFrom, declaredPlayerId,
                validateSender: true);
    }
}
