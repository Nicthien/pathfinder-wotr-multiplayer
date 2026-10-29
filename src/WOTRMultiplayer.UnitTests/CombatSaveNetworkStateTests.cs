using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.UnitTests
{
    public class CombatSaveNetworkStateTests
    {
        private static CombatSaveManifest Valid() => new()
        {
            Unit = "unit", Round = 2,
            Seeds = new() { ["session"] = 1, ["load"] = 2, ["area"] = 3, ["combat"] = 4, ["turn"] = 5 },
            NetworkCombatJson = JsonConvert.SerializeObject(new NetworkCombat
            {
                Seed = 4, Round = 2, Stage = NetworkCombatStage.Running,
                Turn = new NetworkCombatTurn { UnitId = "unit", Seed = 5, Stage = NetworkCombatTurnStage.Playing }
            })
        };
        [Test] public void CompleteTurnCanBePrevalidatedWithoutEngine()
            => Assert.That(CombatSaveNetworkState.Read(Valid()).Turn.Seed, Is.EqualTo(5));

        [Test] public void NonEmptyCollectionsAndConsumedTurnIdentitySurviveRoundTrip()
        {
            var metadata = Valid();
            var original = CombatSaveNetworkState.Read(metadata);
            original.ConfirmedMidCombatUnits.Add("summoned-unit");
            original.RemotelyKilledUnits.Add("dead-enemy");
            original.UntargetableUnits["group"] = new() { "waiting-unit" };
            original.PlayersNextTurnInitialization["summoned-unit"] = new() { 42 };
            original.Turn.PlayersEndTurnInitialization.Add(42);
            metadata.NetworkCombatJson = JsonConvert.SerializeObject(original);
            var restored = CombatSaveNetworkState.Read(metadata);
            Assert.That(restored.ConfirmedMidCombatUnits, Is.EquivalentTo(new[] { "summoned-unit" }));
            Assert.That(restored.RemotelyKilledUnits, Is.EquivalentTo(new[] { "dead-enemy" }));
            Assert.That(restored.UntargetableUnits["group"], Is.EquivalentTo(new[] { "waiting-unit" }));
            Assert.That(restored.PlayersNextTurnInitialization["summoned-unit"], Is.EquivalentTo(new long[] { 42 }));
            Assert.That(restored.Turn.PlayersEndTurnInitialization, Is.EquivalentTo(new long[] { 42 }));
            Assert.That(restored.Turn.Seed, Is.EqualTo(5));
        }

        [TestCase("PlayersNextTurnInitialization")]
        [TestCase("PlayersInCombat")]
        [TestCase("ConfirmedMidCombatUnits")]
        [TestCase("UntargetableUnits")]
        [TestCase("TriggeredAreaEffects")]
        public void NullCollectionsAreRejectedBeforeWorldDisposal(string field)
        {
            var metadata = Valid(); var json = JObject.Parse(metadata.NetworkCombatJson);
            json[field] = JValue.CreateNull(); metadata.NetworkCombatJson = json.ToString();
            Assert.Throws<InvalidDataException>(() => CombatSaveNetworkState.Read(metadata));
        }

        [TestCase("LockCounter", 1)]
        [TestCase("Seed", 999)]
        public void PendingActionOrWrongTurnCannotBeSilentlyNormalized(string field, int value)
        {
            var metadata = Valid(); var json = JObject.Parse(metadata.NetworkCombatJson);
            json["Turn"][field] = value; metadata.NetworkCombatJson = json.ToString();
            Assert.Throws<InvalidDataException>(() => CombatSaveNetworkState.Read(metadata));
        }
        [Test] public void MissingOwnershipRoleIsRejected()
        {
            var metadata = Valid(); metadata.Owners["unit"] = 42;
            Assert.Throws<InvalidDataException>(() => CombatSaveNetworkState.Read(metadata));
        }
    }
}
