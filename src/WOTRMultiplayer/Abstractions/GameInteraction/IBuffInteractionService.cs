using Kingmaker.EntitySystem.Entities;
using WOTRMultiplayer.Entities.Units;
using Kingmaker.UnitLogic.Buffs;

namespace WOTRMultiplayer.Abstractions.GameInteraction
{
    public interface IBuffInteractionService
    {
        NetworkUnitBuffCollection GetUnitBuffs(UnitEntityData combatUnit);

        bool CanRecreateBuff(NetworkBuff buff);

        Buff RestoreNativeBuffForCheckpoint(UnitEntityData unit, NetworkBuff buff);

        void NormalizeNativeBuffTargetsForCheckpoint(UnitEntityData unit,
            NetworkUnitBuffCollection unitBuffCollection,
            WOTRMultiplayer.Services.GameInteraction.RealTimeBuffRestoreTransaction transaction);

        void UpdateUnitBuffs(UnitEntityData unit, NetworkUnitBuffCollection unitBuffCollection);

        void ReconcileUnitBuffsForModeTransition(UnitEntityData unit,
            NetworkUnitBuffCollection unitBuffCollection);

        void UpdateUnitBuffsPresentation(UnitEntityData unit, NetworkUnitBuffCollection unitBuffCollection);

        string ValidateUnitBuffsPresentation(UnitEntityData unit, NetworkUnitBuffCollection unitBuffCollection);

        void StageMissingUnitBuffsPresentation(UnitEntityData unit, NetworkUnitBuffCollection unitBuffCollection,
            WOTRMultiplayer.Services.GameInteraction.RealTimeBuffRestoreTransaction transaction);
    }
}
