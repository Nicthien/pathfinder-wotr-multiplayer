using System;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Connectivity;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;

namespace WOTRMultiplayer.Abstractions
{
    public interface IMultiplayerClient : IMultiplayerActor
    {
        void Connect(string address, int port);

        void Connect(string code, string password, ExternalServer externalServer);

        bool IsConnecting { get; }

        Action OnNetworkError { get; set; }

        Action<NetworkCharacter> OnCharacterOwnerChanged { get; set; }

        void OnBeforeTryRollRestRandomEncounter();

        bool IsRealTimeCombatActive { get; }

        bool IsRealTimeCombatBarrierActive { get; }

        bool IsSavingBlockedByCombatSync { get; }

        bool IsSavingBlockedAfterRealTimeDisconnect { get; }

        int ResolveRealTimeDiceRoll(string key, Func<int> localRoll);

        string ResolveRealTimeEntityId(string key, Func<string> localId);

        bool SubmitRealTimeCombatCommand(RealTimeCommandKind kind, NetworkUnitMoveTo movement = null, NetworkUnitAttack attack = null, NetworkAbilityUse ability = null);

        bool RequestRealTimePause(bool wantsPause);

        bool TryConsumeAuthoritativeRealTimeAIAction(string unitId, out NetworkAIAction action);

        void ReportRealTimeAIActionFault(string unitId, string actionId, string reason);
    }
}
