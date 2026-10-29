using System.IO;
using System;
using System.Collections.Concurrent;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Newtonsoft.Json.Linq;
using WOTRMultiplayer.Entities.Combat;

namespace WOTRMultiplayer.Services.CombatSaving
{
    internal static class CombatSaveNetworkState
    {
        private sealed class NetworkStateResolver : DefaultContractResolver
        {
            protected override JsonContract CreateContract(Type type)
            {
                var contract = base.CreateContract(type);
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ConcurrentDictionary<,>))
                {
                    // The game's older Json.NET invokes binary-serialization
                    // callbacks on already constructed dictionaries. Their binary
                    // backing array is absent in JSON, causing OnDeserialized to
                    // throw. Normal dictionary construction/population is enough.
                    contract.OnDeserializingCallbacks.Clear();
                    contract.OnDeserializedCallbacks.Clear();
                }
                return contract;
            }
        }
        private static readonly JsonSerializerSettings Settings = new()
        {
            ContractResolver = new NetworkStateResolver(), TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore, MaxDepth = 128
        };

        internal static NetworkCombat Read(CombatSaveManifest metadata)
        {
            if (metadata?.NetworkCombatJson == null || metadata.OwnerIsHost == null || metadata.Owners == null
                || metadata.Seeds == null || new[] { "session", "load", "area", "combat", "turn" }.Any(k => !metadata.Seeds.ContainsKey(k))
                || !metadata.Owners.Keys.OrderBy(k => k).SequenceEqual(metadata.OwnerIsHost.Keys.OrderBy(k => k)))
                throw new InvalidDataException("combat-save-resume-metadata-missing");
            GameInteraction.RealTimeNativeCheckpointLoadSource.ValidateJson(metadata.NetworkCombatJson, true);
            var root = JObject.Parse(metadata.NetworkCombatJson);
            var required = new[] { "Seed", "Round", "Stage", "Turn", "PlayersNextTurnInitialization", "PlayersNextTurnSynchronization",
                "PlayersFinishedStartupSequence", "PlayersInCombat", "InitialUnitsInCombat", "UnavailableUnits",
                "MidCombatUnitJoins", "ConfirmedMidCombatUnits", "UntargetableUnits", "TriggeredAreaEffects", "RemotelyKilledUnits" };
            if (required.Any(field => root[field] == null || root[field].Type == JTokenType.Null))
                throw new InvalidDataException("combat-save-network-state-incomplete");
            var turn = root["Turn"] as JObject;
            if (turn == null || new[] { "Seed", "UnitId", "IsAI", "Stage", "LockCounter", "AIActions",
                "PlayersEndTurnInitialization", "PlayersEndTurnSynchronization" }
                .Any(field => turn[field] == null || turn[field].Type == JTokenType.Null))
                throw new InvalidDataException("combat-save-network-turn-incomplete");
            var combat = JsonConvert.DeserializeObject<NetworkCombat>(metadata.NetworkCombatJson, Settings);
            if (combat?.Turn == null || combat.Turn.UnitId != metadata.Unit || combat.Round != metadata.Round
                || combat.Stage != NetworkCombatStage.Running || combat.Turn.Stage != NetworkCombatTurnStage.Playing
                || combat.Turn.IsAI || combat.Turn.LockCounter != 0
                || combat.Turn.Seed != metadata.Seeds["turn"] || combat.Seed != metadata.Seeds["combat"])
                throw new InvalidDataException("combat-save-network-turn-mismatch");
            if (combat.PlayersNextTurnInitialization == null || combat.PlayersNextTurnSynchronization == null
                || combat.PlayersFinishedStartupSequence == null || combat.PlayersInCombat == null
                || combat.InitialUnitsInCombat == null || combat.UnavailableUnits == null
                || combat.MidCombatUnitJoins == null || combat.ConfirmedMidCombatUnits == null
                || combat.UntargetableUnits == null || combat.TriggeredAreaEffects == null
                || combat.RemotelyKilledUnits == null || combat.Turn.AIActions == null
                || combat.Turn.PlayersEndTurnInitialization == null || combat.Turn.PlayersEndTurnSynchronization == null)
                throw new InvalidDataException("combat-save-network-state-incomplete");
            return combat;
        }
    }
}
