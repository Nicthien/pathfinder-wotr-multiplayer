using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Services.CombatSaving;
using WOTRMultiplayer.Services.Random;

namespace WOTRMultiplayer.Services
{
    public abstract partial class MultiplayerActorBase
    {
        private protected CombatSaveManifest PendingCombatSave;
        private string _pendingCombatSaveTurn;
        private Dictionary<string, int> _combatSaveSemanticSeeds;
        private bool _combatSaveGameLoaded;
        private bool _combatSaveSupplementFinalized;
        private DateTime _combatSaveSupplementFinalizedAt;

        private protected bool IsCombatSaveRestoreReady => PendingCombatSave != null
            && _combatSaveGameLoaded
            && (CombatSaveDigestReader != null
                || CombatSaveLoadState.HoldsWorld(Kingmaker.Game.Instance?.State)
                    && _combatSaveSupplementFinalized
                    && DateTime.UtcNow - _combatSaveSupplementFinalizedAt >= TimeSpan.FromMilliseconds(250)
                    && TurnBasedSaveNativeState.IsRestoreVerificationLifecycleReady(_combatSaveGameLoaded,
                        TurnBased.Controllers.CombatController.IsInTurnBasedCombat())
                    && TurnBasedSaveNativeState.IsRestoreCaptureReady(PendingCombatSave.Unit));

        private protected string CombatSaveRestoreReadiness
            => PendingCombatSave == null ? "no-pending-save"
                : !_combatSaveGameLoaded ? "native-load-lifecycle-not-complete"
                : CombatSaveDigestReader != null ? "test-digest-reader-ready"
                : TurnBasedSaveNativeState.DescribeRestoreReadiness(PendingCombatSave.Unit);

        private protected void PrepareCombatSaveLoad(string path)
        {
            _combatSaveGameLoaded = false;
            _combatSaveSupplementFinalized = false;
            _combatSaveSupplementFinalizedAt = DateTime.MinValue;
            if (!File.Exists(path) || !CombatSaveArchive.ContainsManifest(path))
            { PendingCombatSave = null; _pendingCombatSaveTurn = null; _combatSaveSemanticSeeds = null;
                if (ReferenceEquals(CombatSaveLoadState.Owner, this)) CombatSaveLoadState.Owner = null; return; }
            if (!CombatSaveActivation.Enabled) throw new InvalidOperationException("combat-save-restoration-not-validated");
            var content = GetInstalledContent();
            var metadata = CombatSaveArchive.ValidateFile(path, NetworkRealTimeCombatState.ProtocolVersion,
                content.RealTimeBuildFingerprint, Kingmaker.GameVersion.GetVersion(),
                CombatSaveCompatibility.AcceptedArchiveFingerprints);
            _ = CombatSaveNetworkState.Read(metadata);
            // Validate generator representation without changing live streams.
            new DeterministicValueGenerator(null, null, null).RestoreCheckpoint(metadata.Generators);
            var native = CombatSaveArchive.ReadNativeTurn(path);
            _ = CombatSaveNativeSupplement.Read(Newtonsoft.Json.Linq.JObject.Parse(native));
            _ = TurnBasedSaveDigest.Project(Newtonsoft.Json.Linq.JObject.Parse(native));
            PendingCombatSave = metadata; _pendingCombatSaveTurn = native;
            CombatSaveLoadState.Owner = this;
            Logger.LogInformation("[CombatSave] Load preflight passed; native ticking held. Unit={Unit}, Round={Round}", metadata.Unit, metadata.Round);
        }

        // Owlcat keeps finalizing native units for several frames after
        // OnGameLoaded. Restore the fields omitted by its serializer only after
        // the native turn is completely idle, while the load barrier still owns
        // and pauses the world. A later digest check still protects every field.
        private protected bool TryFinalizeCombatSaveLoadedState()
        {
            if (PendingCombatSave == null || CombatSaveDigestReader != null
                || !_combatSaveGameLoaded || _combatSaveSupplementFinalized
                || !CombatSaveLoadState.HoldsWorld(Kingmaker.Game.Instance?.State)
                || !TurnBasedSaveNativeState.IsRestoreVerificationLifecycleReady(_combatSaveGameLoaded,
                    TurnBased.Controllers.CombatController.IsInTurnBasedCombat())
                || !TurnBasedSaveNativeState.IsRestoreCaptureReady(PendingCombatSave.Unit))
                return false;
            CombatSaveNativeSupplement.Read(Newtonsoft.Json.Linq.JObject.Parse(_pendingCombatSaveTurn))
                .RestoreUnitActions(restoreSavedPositions: true);
            _combatSaveSupplementFinalized = true;
            _combatSaveSupplementFinalizedAt = DateTime.UtcNow;
            Logger.LogInformation("[CombatSave] Post-load native finalization settled; omitted combat fields reapplied under the restoration barrier.");
            return true;
        }

        private protected bool RestoreCombatSaveAfterAreaLoaded()
        {
            if (PendingCombatSave == null) return false;
            var metadata = PendingCombatSave;
            CombatSaveLoadState.BindLoadedWorld(Kingmaker.Game.Instance.State);
            CombatSavePauseLease.FreezeClock(Kingmaker.Game.Instance);
            GameInteraction.SetPause(true);
            try
            {
                TurnBasedSaveNativeRestore.Restore(_pendingCombatSaveTurn, metadata.Unit, metadata.Round);
                var combat = CombatSaveNetworkState.Read(metadata);
                combat.PlayersNextTurnInitialization.Clear(); combat.PlayersNextTurnSynchronization.Clear();
                combat.PlayersFinishedStartupSequence.Clear(); combat.PlayersInCombat.Clear();
                combat.InitialUnitsInCombat.Clear(); combat.UnavailableUnits.Clear(); combat.MidCombatUnitJoins.Clear();
                combat.Turn.PlayersEndTurnInitialization.Clear(); combat.Turn.PlayersEndTurnSynchronization.Clear();
                combat.Turn.LockCounter = 0;
                combat.Stage = NetworkCombatStage.Running;
                combat.IsInitiated = combat.IsDataCollected = combat.IsDataCompared = combat.IsSynced = combat.IsStarted = true;
                foreach (var player in Game.Players)
                { combat.PlayersFinishedStartupSequence[player.Id] = true; combat.PlayersInCombat[player.Id] = true; }
                foreach (var entry in metadata.OwnerIsHost)
                {
                    var owner = Game.Players.FirstOrDefault(p => p.IsHost == entry.Value) ?? GetHost();
                    Game.CharactersOwnershipHistory[entry.Key] = owner.Id;
                    var character = Game.Characters.FirstOrDefault(c => c.UnitId == entry.Key);
                    if (character != null) character.Owner = owner;
                }
                combat.Turn.IsLocalPlayer = IsControlledByLocalPlayer(combat.Turn.UnitId);
                Game.Combat = combat; Game.LastCombatTurn = null;
                Game.CurrentArea.Seed = metadata.Seeds["area"];
                Game.RealTimeCombat.Mode = CombatMode.TurnBased;
                Game.RealTimeCombat.Epoch = Math.Max(1, (long)(uint)Game.LoadedSaveSeed);
                _combatSaveSemanticSeeds = new(metadata.Seeds);
                ((DeterministicValueGenerator)ValueGenerator).RestoreCheckpoint(metadata.Generators);
                // OnAreaLoaded is raised while LoadingProcess still owns the
                // native world. Reconstruct the turn here, but do not inspect
                // the final topology until the engine raises OnGameLoaded.
                Logger.LogInformation("[CombatSave] Native turn restored without StartTurn. Unit={Unit}, Round={Round}; awaiting OnGameLoaded verification.", metadata.Unit, metadata.Round);
                return true; // Keep loading lease until BOTH peers confirm this digest.
            }
            catch (Exception error)
            {
                Logger.LogError(error, "[CombatSave] Restore failed; do not resume this world. Load another safe save.");
                GameInteraction.SetPause(true);
                throw;
            }
        }

        private protected Dictionary<string, int> CaptureCombatSaveSeeds()
        {
            var seeds = _combatSaveSemanticSeeds != null ? new Dictionary<string, int>(_combatSaveSemanticSeeds)
                : new() { ["session"] = Game.SessionSeed, ["load"] = Game.LoadedSaveSeed, ["area"] = Game.CurrentArea.Seed };
            // A second save may occur several turns after loading the first one.
            // Only the session/load namespace is retained, never an old turn seed.
            seeds["combat"] = Game.Combat.Seed;
            seeds["turn"] = Game.Combat.Turn.Seed ?? throw new InvalidOperationException("combat-save-no-turn-seed");
            return seeds;
        }

        private protected void VerifyCombatSaveRestore()
        {
            if (PendingCombatSave == null) return;
            if (!IsCombatSaveRestoreReady)
                throw new InvalidOperationException("combat-save-loaded-world-not-ready");
            if (CaptureCombatSaveRestoreDigest() != PendingCombatSave.RestoreDigest)
                throw new InvalidDataException("combat-save-state-changed-before-resume");
        }

        private protected void CompleteCombatSaveRestore()
        {
            if (PendingCombatSave == null) return;
            VerifyCombatSaveRestore();
            if (ReferenceEquals(CombatSaveLoadState.Owner, this)) CombatSaveLoadState.Owner = null;
            PendingCombatSave = null; _pendingCombatSaveTurn = null;
            _combatSaveGameLoaded = false; _combatSaveSupplementFinalized = false;
            _combatSaveSupplementFinalizedAt = DateTime.MinValue;
            Game.ForcedPause = null;
            GameInteraction.SetPause(false); // Turn-based mode remains deterministic; release the load hold without consuming End Turn.
            Logger.LogInformation("[CombatSave] Both worlds verified; restoration barrier released and combat resumed.");
            PlayerNotification.ShowWarningNotification(WellKnownKeys.LobbyWindow.RealTime.SaveCombatReady.Key, addToLog: false);
        }
    }
}
