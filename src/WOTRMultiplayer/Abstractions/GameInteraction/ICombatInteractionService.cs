using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Combat.Crusades;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Abstractions.GameInteraction
{
    public interface ICombatInteractionService
    {
        bool IsInCombat();

        bool IsInCombat(string unitId);

        bool IsInCrusadeTacticalCombat();

        NetworkCombatState GetCombatState();

        NetworkCombatState GetRealTimeCombatState();

        Task<NetworkCombatState> GetRealTimeCombatStateAsync();

        List<NetworkUnit> GetUnitsInCombat();

        List<NetworkUnit> GetParty();

        void UpdateUnits(List<NetworkUnit> networkUnits, bool updatePosition);

        Task<bool> UpdateUnitsAsync(List<NetworkUnit> networkUnits, bool updatePosition);

        void StartTurnBasedCombatTurn(string unitId, Action onInvalidUnit = null);

        void EndCombatTurn(string unitId);

        bool IsCombatTurnFinished();

        void DelayCombatTurn(string unitId, string targetUnitId);

        Task UpdateCombatStateAsync(NetworkCombatState networkCombatState, List<NetworkAreaEffect> areaEffects, bool requiresFullUpdate);

        Task UpdateRealTimeCombatStateAsync(NetworkCombatState networkCombatState);

        Task<NetworkCombatState> ApplyAndReadRealTimeCombatStateAsync(NetworkCombatState networkCombatState);

        Task<NetworkCombatState> TryApplyAndReadRealTimeCombatStateAsync(
            NetworkCombatState networkCombatState, Func<bool> canApply);

        void InitializeCrusadeArmyCombat();

        int GetCrusadeArmyCombatAreaSeed();

        void RunTacticalUnitAttackCommand(NetworkTacticalUnitAttackCommand tacticalUnitAttackCommand);

        void RunTacticalUnitUseAbilityCommand(NetworkTacticalUnitUseAbilityCommand tacticalUnitUseAbilityCommand);

        void RunTacticalUnitMoveToCommand(NetworkTacticalUnitMoveToCommand tacticalUnitMoveToCommand);

        void UseTacticalCombatTotalDefense();

        void PostponeTacticalCombatTurn();

        void RetreatFromTacticalCombat();

        void AttackUnit(NetworkUnitAttack attack);

        void UseAbility(NetworkAbilityUse networkAbilityUse);

        bool ExecuteRealTimeCombatCommand(RealTimeCommandKind kind, NetworkUnitMoveTo movement, NetworkUnitAttack attack, NetworkAbilityUse ability);

        Task<bool?> TryPresentRealTimeActionAsync(
            WOTRMultiplayer.Networking.Messages.Game.NotifyRealTimeAuthoritativeEvent authoritativeEvent,
            Func<bool> canPresent);

        void TickRealTimePresentation(bool active);

        Task<bool> ExecuteRealTimeCombatCommandAsync(RealTimeCommandKind kind, NetworkUnitMoveTo movement, NetworkUnitAttack attack, NetworkAbilityUse ability);

        Task<bool?> TryExecuteRealTimeCombatCommandAsync(RealTimeCommandKind kind,
            NetworkUnitMoveTo movement, NetworkUnitAttack attack, NetworkAbilityUse ability,
            Func<bool> canExecute);

        Task<bool> StartCombatAsync(NetworkCombatState networkCombatState);

        Task<List<string>> EnsureUnitsInCombatAsync(List<string> units);

        void AddUnitsToCombat(List<string> units);

        bool KillUnit(NetworkPlayer player, string unitId);

        bool IsRiderActive();

        void MoveUnit(NetworkUnitMoveTo unitMoveTo);

        void MakeUnitTargetable(string unitId);

        void MakeUnitUntargetable(string unitId);

        bool AreThereAnyProjectilesLaunchedByParty();

        bool HasActiveRealTimeProjectiles();

        bool HasActiveRealTimeAtomicActions();

        Task<bool?> TryApplyRealTimeUnitDeathAsync(string unitId, NetworkPlayer hostPlayer, Func<bool> canApply);

        Task<bool> TryPauseAtProjectileSafePointAsync(Func<bool> canPause, Action pause);

        void InteractWithUnit(NetworkUnitInteractWithUnit networkUnitInteractWithUnit);

        void LootUnit(NetworkUnitLootUnit networkUnitLootUnit);

        void ForceResetCombat();

        Task ForceResetCombatAsync();

        bool IsCombatInitialized();

        Task ExcludeUnitsFromCombatAsync(List<string> units);

        void SetTacticalCombatAcceleration(bool isAccelerated);

        Task KillUnitAndResetTurnAsync(NetworkPlayer networkPlayer, string unitId);

        bool IsActiveArmyCombatUnitBusy();
        Task UpdateArmyCombatUnitsAsync(List<NetworkUnit> units);
    }
}
