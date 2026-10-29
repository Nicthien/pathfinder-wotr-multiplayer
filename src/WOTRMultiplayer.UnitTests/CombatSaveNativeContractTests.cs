using System.Linq;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NUnit.Framework;
using TurnBased.Controllers;
using HarmonyLib;
using Kingmaker.Designers.Mechanics.Facts;
using Kingmaker.UnitLogic.FactLogic;
using WOTRMultiplayer.HarmonyPatches.Combat;
using WOTRMultiplayer.Services.CombatSaving;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.UnitTests
{
    public class CombatSaveNativeContractTests
    {
        internal static CombatSaveNativeSupplement Supplement() => new()
        {
            RoundNumber = 2, Initialized = true,
            UnitActions = new() { new() { Unit = "unit", Move = 3, Standard = 6,
                GroupCounterCaptured = true, GroupCombatCounter = 2,
                Resources = new() { new NetworkAbilityResource { BlueprintId = "resource", Amount = 2, RetainCount = 1 } } } },
            UnitRelations = new() { new() { Unit = "unit", Engaged = new(), EngagedBy = new(),
                DisengageTargets = System.Array.Empty<string>(), StartPosition = new float[] { 1, 2, 3 } } },
            UnitAi = new() { new() { Unit = "unit", Unreachable = System.Array.Empty<string>(), Hard = System.Array.Empty<string>(),
                Disliked = System.Array.Empty<string>(), Bother = System.Array.Empty<string>(), Actions = new(), IneffectiveAttackHistoryCount = 0 } }
        };

        [Test]
        public void SupplementRejectsMissingStateAndInvalidReferencesBeforeLoading()
        {
            var native = new JObject(); var supplement = Supplement();
            supplement.AddTo(native);
            var restored = CombatSaveNativeSupplement.Read(native).UnitActions.Single();
            Assert.That(restored.Standard, Is.EqualTo(6));
            Assert.That(restored.GroupCombatCounter, Is.EqualTo(2));
            Assert.That(restored.Resources.Single().Amount, Is.EqualTo(2));
            native["MultiplayerControllerState"]["Mount"] = "missing-mount";
            Assert.Throws<System.IO.InvalidDataException>(() => CombatSaveNativeSupplement.Read(native));
            supplement.AddTo(native);
            ((JObject)native["MultiplayerControllerState"]).Remove("CanGetUpOnCommand");
            Assert.Throws<Newtonsoft.Json.JsonSerializationException>(() => CombatSaveNativeSupplement.Read(native));
            supplement.AddTo(native);
            ((JObject)native["MultiplayerControllerState"]["UnitAi"][0]).Remove("StartTime");
            Assert.Throws<Newtonsoft.Json.JsonSerializationException>(() => CombatSaveNativeSupplement.Read(native));
            supplement.AddTo(native);
            ((JObject)native["MultiplayerControllerState"]["UnitAi"][0]).Remove("IneffectiveAttackHistoryCount");
            Assert.Throws<Newtonsoft.Json.JsonSerializationException>(() => CombatSaveNativeSupplement.Read(native));
            supplement.AddTo(native);
            native["MultiplayerControllerState"]["UnitRelations"][0]["StartPosition"] = new JArray(1, 2);
            Assert.Throws<System.IO.InvalidDataException>(() => CombatSaveNativeSupplement.Read(native));
        }

        [Test]
        public void AiCooldownsAndTargetRelationsSurviveArchiveRepresentation()
        {
            var source = Supplement();
            source.UnitRelations[0].Engaged.Add("unit", 123456);
            source.UnitRelations[0].ManualTarget = "unit";
            source.UnitAi[0].Actions.Add(new CombatSaveAiState.ActionState { Slot = "action:0", Blueprint = "blueprint", Count = 2, Cooldown = 456789 });
            source.UnitAi[0].LastTarget = "unit";
            source.UnitAi[0].IneffectiveAttackHistoryCount = 2;
            var native = new JObject(); source.AddTo(native);
            var restored = CombatSaveNativeSupplement.Read(native);
            Assert.That(restored.UnitRelations[0].Engaged["unit"], Is.EqualTo(123456));
            Assert.That(restored.UnitRelations[0].ManualTarget, Is.EqualTo("unit"));
            Assert.That(restored.UnitAi[0].Actions.Single().Count, Is.EqualTo(2));
            Assert.That(restored.UnitAi[0].Actions.Single().Cooldown, Is.EqualTo(456789));
            Assert.That(restored.UnitAi[0].LastTarget, Is.EqualTo("unit"));
            Assert.That(restored.UnitAi[0].IneffectiveAttackHistoryCount, Is.EqualTo(2));
        }

        [Test]
        public void IneffectiveAttackHistoryUsesFinishedInertMarkersInsteadOfCommandDeserialization()
        {
            var marker = CombatSaveAiState.CreateCompletedIneffectiveAttackMarker(null);

            Assert.That(marker, Is.Not.InstanceOf<Kingmaker.UnitLogic.Commands.UnitAttack>());
            Assert.That(marker, Is.Not.InstanceOf<Kingmaker.UnitLogic.Commands.UnitUseAbility>());
            Assert.That(marker.Result, Is.EqualTo(Kingmaker.UnitLogic.Commands.Base.UnitCommand.ResultType.Success));
            Assert.That(marker.IsFinished, Is.True);
            Assert.That(CombatSaveAiState.CaptureIneffectiveAttackHistory(new[] { marker }), Is.EqualTo(1));
            Assert.Throws<System.IO.InvalidDataException>(() =>
                CombatSaveAiState.CaptureIneffectiveAttackHistory(new Kingmaker.UnitLogic.Commands.Base.UnitCommand[] { null }));
        }
        [Test]
        public void NativeTurnStatusSurvivesDeserialization()
        {
            var saved = Kingmaker.EntitySystem.Persistence.JsonUtility.OwlcatJsonConvert
                .DeserializeObject<CombatController>("{\"m_CurrentTurn\":{\"Status\":\"Acting\"}}");
            Assert.That(saved.CurrentTurn, Is.Not.Null);
            Assert.That(saved.CurrentTurn.Status, Is.EqualTo(TurnController.TurnStatus.Acting));
            var rebound = new TurnController(default(Kingmaker.EntitySystem.Persistence.JsonUtility.JsonConstructorMark));
            TurnBasedSaveNativeRestore.CopyPersistedMembers(saved.CurrentTurn, rebound, null);
            Assert.That(rebound.Status, Is.EqualTo(TurnController.TurnStatus.Acting));
        }

        [Test]
        public void FinalSupplementRestoreReappliesMovementLimitAfterNativeLoaderDefaults()
        {
            var controller = Kingmaker.EntitySystem.Persistence.JsonUtility.OwlcatJsonConvert
                .DeserializeObject<CombatController>("{\"m_CurrentTurn\":{}}");
            var supplement = Supplement();
            supplement.MovementLimit = (TurnController.MovementLimit)1;
            controller.CurrentTurn.CurrentMovementLimit = (TurnController.MovementLimit)0;

            supplement.Restore(controller, controller.CurrentTurn);

            Assert.That(controller.CurrentTurn.CurrentMovementLimit,
                Is.EqualTo(supplement.MovementLimit));
        }

        [Test]
        public void DeserializedNavmeshUpdateShellsAreClearedBeforeFirstRestoredTurnChange()
        {
            var saved = Kingmaker.EntitySystem.Persistence.JsonUtility.OwlcatJsonConvert
                .DeserializeObject<CombatController>("{\"m_Units\":[{\"LastNavmeshUpdateObject\":{}}]}");
            var units = (System.Collections.IEnumerable)AccessTools.Field(typeof(CombatController), "m_Units")
                .GetValue(saved);
            var restoredUnit = units.Cast<object>().Single();
            var navmesh = AccessTools.Field(restoredUnit.GetType(), "LastNavmeshUpdateObject");

            Assert.That(navmesh.GetValue(restoredUnit), Is.Not.Null,
                "The native serializer recreates a non-null but unusable GraphUpdateObject shell");
            Assert.That(TurnBasedSaveNativeRestore.ClearRestoredNavmeshUpdates(saved), Is.EqualTo(1));
            Assert.That(navmesh.GetValue(restoredUnit), Is.Null);
            Assert.That(TurnBasedSaveNativeRestore.ClearRestoredNavmeshUpdates(saved), Is.Zero,
                "Sanitizing an already clean controller must be idempotent");
        }

        [Test]
        public void MotionBaselineSurvivesSupplementSerializationAndRejectsNonFiniteValues()
        {
            var actions = new CombatSaveUnitActions { Unit = "saved-unit", PreviousPositionX = -51.9744f,
                PreviousPositionY = 40.20021f, PreviousPositionZ = -33.92917f, Move = 3, Standard = 6,
                Resources = new() { new NetworkAbilityResource { BlueprintId = "resource", Amount = 0, RetainCount = 1 } } };
            var restored = JObject.FromObject(actions).ToObject<CombatSaveUnitActions>();
            Assert.That(restored.PreviousPositionX, Is.EqualTo(actions.PreviousPositionX));
            Assert.That(restored.PreviousPositionY, Is.EqualTo(actions.PreviousPositionY));
            Assert.That(restored.PreviousPositionZ, Is.EqualTo(actions.PreviousPositionZ));
            Assert.That(restored.Move, Is.EqualTo(3));
            Assert.That(restored.Standard, Is.EqualTo(6));
            Assert.That(restored.IsValid(), Is.True);
            var incomplete = JObject.FromObject(actions);
            incomplete.Remove(nameof(CombatSaveUnitActions.PreviousPositionX));
            Assert.Throws<Newtonsoft.Json.JsonSerializationException>(() => incomplete.ToObject<CombatSaveUnitActions>());
            incomplete = JObject.FromObject(actions);
            incomplete.Remove(nameof(CombatSaveUnitActions.Resources));
            Assert.Throws<Newtonsoft.Json.JsonSerializationException>(() => incomplete.ToObject<CombatSaveUnitActions>());
            restored.PreviousPositionX = float.NaN;
            Assert.That(restored.IsValid(), Is.False);
        }

        [Test]
        public void LoadedPositionBecomesTheIdleMotionBaselineAfterNativeRounding()
        {
            var archived = new UnityEngine.Vector3(25.0079384f, 35.0027466f, -65.5179749f);
            var loaded = new UnityEngine.Vector3(25.00f, 35.00f, -65.51f);

            Assert.That(loaded, Is.Not.EqualTo(archived),
                "The native save may round the authoritative current position");
            Assert.That(CombatSaveUnitActions.LoadedMotionBaseline(loaded), Is.EqualTo(loaded),
                "A save accepted while idle must load with HasMotionThisTick false");
        }

        [Test]
        public void ArchivedIdlePositionRetainsTheAuthoritativeNavmeshHeight()
        {
            var actions = new CombatSaveUnitActions
            {
                Unit = "unit", PreviousPositionX = 29.05f,
                PreviousPositionY = 35.12f, PreviousPositionZ = -63.69f, Resources = new()
            };

            Assert.That(actions.SavedIdlePosition(),
                Is.EqualTo(new UnityEngine.Vector3(29.05f, 35.12f, -63.69f)));
        }

        [TestCase(TurnController.TurnStatus.Preparing, true)]
        [TestCase(TurnController.TurnStatus.None, false)]
        [TestCase(TurnController.TurnStatus.Scrolling, false)]
        [TestCase(TurnController.TurnStatus.Acting, true)]
        [TestCase(TurnController.TurnStatus.Delayed, false)]
        [TestCase(TurnController.TurnStatus.Ending, false)]
        [TestCase(TurnController.TurnStatus.Ended, false)]
        public void OnlyOngoingTurnStatusesCanBeSavedWhenCommandsAreIdle(TurnController.TurnStatus status, bool expected)
            => Assert.That(TurnBasedSaveNativeState.IsStableStatus(status), Is.EqualTo(expected));

        [Test]
        public void RestoredControllerCanBeValidatedBeforeNativeCombatNotification()
        {
            Assert.That(TurnBasedSaveNativeState.IsCombatLifecycleReady(inCombat: false, restoring: true), Is.True);
            Assert.That(TurnBasedSaveNativeState.IsCombatLifecycleReady(inCombat: false, restoring: false), Is.False);
            Assert.That(TurnBasedSaveNativeState.IsCombatLifecycleReady(inCombat: true, restoring: false), Is.True);
        }

        [Test]
        public void RestoredWorldBlocksNativeEngagementRecalculationUntilVerification()
        {
            var engagementTick = AccessTools.Method(
                typeof(Kingmaker.Controllers.Combat.UnitCombatEngagementController), "Tick");

            Assert.That(engagementTick, Is.Not.Null);
            Assert.That(CombatSaveWorldControllerPatches.Targets(), Does.Contain(engagementTick),
                "A transitional CanAct/threat-hand state must not erase restored melee engagements");
        }

        [Test]
        public void DigestFieldsExistInInstalledNativeSerializationContract()
        {
            var resolver = new Kingmaker.EntitySystem.Persistence.JsonUtility.OptInContractResolver();
            JObject Shape(System.Type type) => new(((JsonObjectContract)resolver.ResolveContract(type)).Properties
                .Where(p => !p.Ignored && p.Readable).Select(p => new JProperty(p.PropertyName, JValue.CreateNull())));
            var combat = Shape(typeof(CombatController));
            Assert.That(combat.Property("m_CurrentTurn"), Is.Not.Null, combat.ToString());
            combat["m_CurrentTurn"] = Shape(typeof(TurnController));
            combat["m_Units"] = new JArray();
            Assert.That(combat.Property("RoundNumber"), Is.Null,
                "The native serializer omits round state; the supplement is mandatory");
            Supplement().AddTo(combat);
            Assert.DoesNotThrow(() => TurnBasedSaveDigest.Project(combat));
        }

        [Test]
        public void UnitCombatCountersNeedAnExplicitSupplement()
        {
            var contract = (JsonObjectContract)new Kingmaker.EntitySystem.Persistence.JsonUtility.OptInContractResolver()
                .ResolveContract(typeof(Kingmaker.EntitySystem.Entities.UnitEntityData));
            Assert.That(contract.Properties.Any(property => property.PropertyName == "CombatState" && !property.Ignored), Is.False);
        }

        [Test]
        public void CapturePatchesCoverObservedDerivedFactAndAdditionalLimbCallbacks()
        {
            Assert.That(AccessTools.Method(typeof(AddFeatureOnSkill), "OnActivate"), Is.Not.Null);
            Assert.That(AccessTools.Method(typeof(Kingmaker.Designers.Mechanics.Buffs.BuffStatusCondition), "OnTurnOff"), Is.Not.Null);
            Assert.That(AccessTools.Method(typeof(Kingmaker.Designers.Mechanics.Buffs.BuffStatusCondition), "OnTurnOn"), Is.Not.Null);
            Assert.That(AccessTools.Method(typeof(AddAdditionalLimb), "OnTurnOff"), Is.Not.Null);
            Assert.That(AccessTools.Method(typeof(AddAdditionalLimb), "OnTurnOn"), Is.Not.Null);
            Assert.That(AccessTools.Field(typeof(Kingmaker.Items.UnitBody), "m_PolymorphAdditionalLimbs"), Is.Not.Null);
            Assert.That(AccessTools.Field(typeof(Kingmaker.Items.UnitBody), "m_HumanToPolymorphAdditionalLimbsMapping"), Is.Not.Null);
            Assert.That(AccessTools.Field(typeof(Kingmaker.UnitLogic.Commands.Base.UnitCommand), "<Result>k__BackingField"), Is.Not.Null);
            Assert.That(AccessTools.Field(typeof(Kingmaker.UnitLogic.Commands.Base.UnitCommand), "<IsFinished>k__BackingField"), Is.Not.Null);
            Assert.That(typeof(CombatSaveDerivedFactsPatch).GetCustomAttributes(typeof(HarmonyPatch), false), Is.Not.Empty);
            Assert.That(CombatSaveStatusConditionPatch.TargetMethods(), Has.Exactly(2).Items);
            Assert.That(typeof(CombatSaveRememberAdditionalLimbPatch).GetCustomAttributes(typeof(HarmonyPatch), false), Is.Not.Empty);
            Assert.That(typeof(CombatSaveRestoreAdditionalLimbPatch).GetCustomAttributes(typeof(HarmonyPatch), false), Is.Not.Empty);
            Assert.That(CombatSaveDerivedFactsPatch.Prefix(), Is.True,
                "Outside a capture, ordinary skill-derived feature updates must remain enabled");
            Assert.That(CombatSaveStatusConditionPatch.Prefix(), Is.True,
                "Outside a capture, ordinary status-condition updates must remain enabled");
        }

        [Test]
        public void CursorPredictionsDoNotChangeDigestButConsumedActionsDo()
        {
            var actions = new JObject();
            foreach (var name in new[] { "FiveFootStep", "Move", "Standard", "Swift", "Free" })
                actions[name] = new JObject { ["AbilityActivityStateCurrent"] = "Available",
                    ["AttackActivityStateCurrent"] = "Available", ["MovementActivityStateCurrent"] = "Available",
                    ["MaxMoveDistance"] = 30, ["RemainingMoveDistance"] = 30,
                    ["Type"] = "None", ["CanUse"] = true, ["PredictedMoveDistance"] = 0 };
            var turn = new JObject();
            foreach (var key in new[] { "m_Rider", "Status", "m_ActingInSurpriseRound", "m_RiderMovementStats",
                "m_MountMovementStats", "m_AttackMode", "m_MountAttackMode", "m_GroundMovementLimit", "m_MountActionState" })
                turn[key] = JValue.CreateNull();
            turn["m_RiderActionState"] = new JObject { ["IsSurprise"] = false, ["ActionsStates"] = actions };
            var source = new JObject { ["m_CurrentTurn"] = turn, ["RoundNumber"] = 1,
                ["MultiplayerControllerState"] = new JObject { ["UnitActions"] = new JArray(new JObject { ["Standard"] = 0 }) },
                ["m_HasSurpriseRound"] = false, ["m_KnownInitiative"] = new JObject(), ["m_Units"] = new JArray() };
            var digest = TurnBasedSaveDigest.Project(source);
            actions["Move"]["Type"] = "Move"; actions["Move"]["CanUse"] = false;
            actions["Move"]["PredictedMoveDistance"] = 30;
            turn["m_RiderActionState"]["ApproachPoint"] = "local-cursor";
            Assert.That(TurnBasedSaveDigest.Project(source), Is.EqualTo(digest));
            actions["Move"]["MovementActivityStateCurrent"] = "Used";
            Assert.That(TurnBasedSaveDigest.Project(source), Is.Not.EqualTo(digest));
            actions["Move"]["MovementActivityStateCurrent"] = "Available";
            source["MultiplayerControllerState"]["UnitActions"][0]["Standard"] = 6;
            Assert.That(TurnBasedSaveDigest.Project(source), Is.Not.EqualTo(digest));
            var counters = source["MultiplayerControllerState"]["UnitActions"][0];
            source["MultiplayerControllerState"]["TimeToNextRound"] = 5.454545;
            var clockDigest = TurnBasedSaveDigest.Project(source);
            source["MultiplayerControllerState"]["TimeToNextRound"] = 5.4545455;
            Assert.That(TurnBasedSaveDigest.Project(source), Is.EqualTo(clockDigest));
            Assert.That((double)source["MultiplayerControllerState"]["TimeToNextRound"], Is.EqualTo(5.4545455));
            source["MultiplayerControllerState"]["TimeToNextRound"] = 0;
            clockDigest = TurnBasedSaveDigest.Project(source);
            source["MultiplayerControllerState"]["TimeToNextRound"] = 0.000001;
            Assert.That(TurnBasedSaveDigest.Project(source), Is.Not.EqualTo(clockDigest));
            counters["Move"] = 4.02225542;
            var accumulated = TurnBasedSaveDigest.Project(source);
            counters["Move"] = 4.022262;
            Assert.That(TurnBasedSaveDigest.Project(source), Is.EqualTo(accumulated));
            Assert.That((double)counters["Move"], Is.EqualTo(4.022262), "Projection must not alter archived host counters");
            foreach (var boundary in new[] { 0d, 3d, 6d })
            {
                counters["Move"] = boundary;
                var atBoundary = TurnBasedSaveDigest.Project(source);
                counters["Move"] = boundary + 0.000001;
                Assert.That(TurnBasedSaveDigest.Project(source), Is.Not.EqualTo(atBoundary),
                    "Numerical tolerance must preserve the available-action boundary");
            }
            source["m_Units"] = new JArray(new JObject
            {
                ["$id"] = "24",
                ["Initiative"] = 20,
                ["LastNavmeshUpdateObject"] = new JObject { ["$id"] = "23" }
            });
            var navmeshDigest = TurnBasedSaveDigest.Project(source);
            source["m_Units"][0]["$id"] = "23";
            source["m_Units"][0]["LastNavmeshUpdateObject"] = JValue.CreateNull();
            Assert.That(TurnBasedSaveDigest.Project(source), Is.EqualTo(navmeshDigest),
                "Unusable navmesh shells and shifted serializer reference ids must not reject an exact rebound");
            source["m_Units"][0]["Initiative"] = 19;
            Assert.That(TurnBasedSaveDigest.Project(source), Is.Not.EqualTo(navmeshDigest),
                "Mechanical unit state must remain protected by the rebound digest");
        }

        [Test]
        public void MetadataCannotBeMistakenForNativeAreaJson()
            => Assert.That(CombatSaveArchive.ManifestName.EndsWith(".json"), Is.False);

        [Test]
        public void ReboundDifferenceNamesTheFirstMechanicalField()
        {
            var expected = "{\"controller\":{\"UnitActions\":[{\"Move\":3}]},\"round\":1}";
            var actual = "{\"controller\":{\"UnitActions\":[{\"Move\":6}]},\"round\":1}";
            Assert.That(TurnBasedSaveDigest.DescribeFirstDifference(expected, actual),
                Does.StartWith("$.controller.UnitActions[0].Move:expected=3;actual=6"));
        }

        [Test]
        public void CombatSaveDoesNotRequireAnIsolatedProfile()
            => Assert.That(CombatSaveActivation.Enabled, Is.True);

        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, true)]
        public void RestoreVerificationWaitsForLoadAndCombatLifecycle(bool gameLoaded, bool inCombat, bool expected)
            => Assert.That(TurnBasedSaveNativeState.IsRestoreVerificationLifecycleReady(gameLoaded, inCombat),
                Is.EqualTo(expected));
    }
}
