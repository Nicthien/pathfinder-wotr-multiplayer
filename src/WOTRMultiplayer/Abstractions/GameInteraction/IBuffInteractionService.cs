using Kingmaker.EntitySystem.Entities;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Abstractions.GameInteraction
{
    public interface IBuffInteractionService
    {
        NetworkUnitBuffCollection GetUnitBuffs(UnitEntityData combatUnit);

        bool CanRecreateBuff(NetworkBuff buff);

        void UpdateUnitBuffs(UnitEntityData unit, NetworkUnitBuffCollection unitBuffCollection);

        void UpdateUnitBuffsPresentation(UnitEntityData unit, NetworkUnitBuffCollection unitBuffCollection);
    }
}
