using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Kingmaker.GameModes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking.Messages.Game;
using WOTRMultiplayer.Services.CombatSaving;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.Services
{
    public abstract partial class MultiplayerActorBase
    {
        // Keeps Unity state capture separate from the network barrier in protocol tests.
        internal Func<string> CombatSaveDigestReader;
        private protected bool IsCurrentCombatSave(NotifyCombatSave message)
            => message != null && Guid.TryParseExact(message.Operation, "N", out _)
                && Game?.Stage == NetworkLobbyStage.Playing && Game.Id == message.SessionId
                && Game.SessionSeed == message.SessionSeed && Game.LoadedSaveSeed == message.LoadedSaveSeed
                && Game.RealTimeCombat?.Epoch == message.Epoch;

        private protected static CombatSaveIdentity SaveIdentity(NotifyCombatSave message)
            => new(message.SessionId + ":" + message.SessionSeed.ToString(CultureInfo.InvariantCulture),
                message.LoadedSaveSeed.ToString(CultureInfo.InvariantCulture), message.Epoch, Guid.ParseExact(message.Operation, "N"));

        private protected string CombatSaveRefusal()
        {
            if (PrepareMovementScope() && HasPendingTurnMovement) return "movement-synchronization-pending";
            var native = Kingmaker.Game.Instance;
            if (Game?.Stage != NetworkLobbyStage.Playing || native?.Player == null) return "not-playing";
            var refusal = new CombatSaveEligibility
            {
                Host = true, // Caller validates the sender/role separately, on BOTH peers.
                TurnBased = Game.RealTimeCombat?.Mode == CombatMode.TurnBased,
                Running = Game.Combat?.Stage == NetworkCombatStage.Running,
                PlayerTurn = Game.Combat?.Turn?.IsAI == false,
                Initialized = Game.Combat?.Turn?.Stage == NetworkCombatTurnStage.Playing,
                ActionPending = Game.Combat?.Turn?.LockCounter > 0 || CombatInteraction.HasActiveRealTimeAtomicActions()
                    || CombatInteraction.HasActiveRealTimeProjectiles(),
                Transition = Game.RealTimeCombat?.Phase != CombatSynchronizationPhase.Stable
                    || Game.RealTimeCombat.CombatEndPending || Game.ForcedPause != null,
                Repair = RealTimeNativeGraphRepair.RequiresRebuild(native.State),
                Dialog = native.IsModeActive(GameModeType.Dialog),
                Cutscene = native.IsModeActive(GameModeType.Cutscene),
                Loading = native.SaveManager.CommitInProgress,
                Rejoining = SessionRejoinRuntime.Blocked || RealTimeNativeLoadLease.IsActive
            }.Refusal();
            if (refusal != null) return refusal;
            if (native.CurrentMode != GameModeType.Default && native.CurrentMode != GameModeType.Pause)
                return "native-mode-not-ready";
            if (native.TurnBasedCombatController?.CurrentTurn?.Rider?.UniqueId != Game.Combat.Turn.UnitId)
                return "native-turn-mismatch";
            return null;
        }

        private protected string CaptureCombatSaveDigest()
            => CaptureCombatSaveDigest(restoreProjection: false);

        private protected string CaptureCombatSaveRestoreDigest()
            => CombatSaveDigestReader != null ? CombatSaveDigestReader()
                : CaptureCombatSaveDigest(restoreProjection: true);

        private string CaptureCombatSaveDigest(bool restoreProjection)
        {
            if (CombatSaveDigestReader != null) return CombatSaveDigestReader();
            using var combatSaveProjection = new CombatSaveDigestScope();
            // Before publication compare the synchronized combat projection.
            // The presentation client deliberately owns inert replicas rather
            // than an identical native graph.  After loading the host archive,
            // however, both peers must match the complete native reference.
            var reference = restoreProjection
                ? CombatInteraction.GetRealTimeNativeReferenceState()
                : CombatInteraction.GetRealTimeCombatState();
            var world = restoreProjection ? CombatSaveRestoreHasher.Compute(reference)
                : RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = reference });
            var json = JObject.Parse(TurnBasedSaveNativeState.Capture());
            var mechanics = TurnBasedSaveDigest.Project(json);
            if (CombatSaveActivation.Enabled)
            {
                // One bounded local diagnostic per actor, only in the isolated lab.
                // Never weaken the comparison to hide a peer mismatch.
                try
                {
                    var path = System.IO.Path.Combine(Kingmaker.Utility.ApplicationPaths.persistentDataPath,
                        "combat-save-digest-" + GetType().Name + (restoreProjection ? "-restore" : ""));
                    var diagnostic = new JObject { ["worldHash"] = world, ["mechanics"] = JObject.Parse(mechanics) };
                    System.IO.File.WriteAllText(path + ".json", diagnostic.ToString(Formatting.Indented));
                    var referenceJson = JsonConvert.SerializeObject(reference, Formatting.None);
                    if (referenceJson.Length <= 64 * 1024 * 1024)
                        System.IO.File.WriteAllText(path + "-reference.json", referenceJson);
                }
                catch (System.IO.IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return CombatSaveArchive.Hash(Encoding.UTF8.GetBytes(world + "\n" + mechanics));
        }
    }
}
