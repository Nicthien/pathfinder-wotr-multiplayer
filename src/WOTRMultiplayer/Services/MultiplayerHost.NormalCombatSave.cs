using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Kingmaker.EntitySystem;
using Kingmaker.TurnBasedMode;
using Kingmaker.UnitLogic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Services.CombatSaving;
using WOTRMultiplayer.Services.GameInteraction;
using WOTRMultiplayer.Services.Random;

namespace WOTRMultiplayer.Services
{
    public partial class MultiplayerHost
    {
        // Called by the native tbm.json writer. This is a host snapshot, not a
        // peer-synchronized save barrier: native save restrictions still apply,
        // but mod synchronization state never refuses the ordinary write.
        internal CombatSaveManifest CaptureNormalCombatSave(ref string nativeTurn)
        {
            if (!IsActive || Game?.Stage != NetworkLobbyStage.Playing
                || Game.Combat?.Stage != NetworkCombatStage.Running
                || Game.RealTimeCombat?.Mode != CombatMode.TurnBased)
                return null;
            if (Game.Combat.Turn?.Stage != NetworkCombatTurnStage.Playing)
            {
                Logger.LogInformation("[CombatSave] Native combat save remains ordinary; resume manifest is skipped before the network turn reaches Playing. Stage={Stage}",
                    Game.Combat.Turn?.Stage);
                return null;
            }
            if (Game.Combat.Turn.IsAI || Game.Combat.Turn.LockCounter != 0
                || Game.Combat.Turn.Seed == null || string.IsNullOrWhiteSpace(nativeTurn))
                return null;
            var controller = Kingmaker.Game.Instance?.TurnBasedCombatController;
            if (controller?.CurrentTurn?.Rider?.UniqueId != Game.Combat.Turn.UnitId) return null;

            var native = JObject.Parse(nativeTurn);
            var supplement = CombatSaveNativeSupplement.Capture(controller);
            supplement.AddTo(native);
            nativeTurn = native.ToString(Formatting.None);

            NetworkCombatState mechanical;
            using (var projection = new CombatSaveDigestScope())
                mechanical = CombatInteraction.GetRealTimeCombatState();
            CombatInteractionService.RetainNativeCombatUnits(mechanical,
                Kingmaker.Game.Instance.State.Units.All.Where(unit => unit.CombatState?.IsInCombat == true).Select(unit => unit.UniqueId));
            var mechanics = TurnBasedSaveDigest.Project(native);
            string Digest(string world) => CombatSaveArchive.Hash(
                Encoding.UTF8.GetBytes(world + "\n" + mechanics));
            var generator = ValueGenerator as DeterministicValueGenerator
                ?? throw new NotSupportedException("combat-save-generator-unsupported");
            var content = GetInstalledContent();
            var manifest = new CombatSaveManifest
            {
                Protocol = NetworkRealTimeCombatState.ProtocolVersion,
                BuildFingerprint = content.RealTimeBuildFingerprint,
                GameVersion = Kingmaker.GameVersion.GetVersion(), Campaign = Game.Id,
                Round = Game.Combat.Round, Unit = Game.Combat.Turn.UnitId,
                CanonicalDigest = Digest(RealTimeCombatHasher.Compute(
                    new RealTimeCombatSnapshot { Combat = mechanical }).ToString()),
                RestoreDigest = Digest(CombatSaveRestoreHasher.Compute(mechanical).ToString()),
                Generators = generator.CaptureCheckpoint(),
                Owners = Game.Characters.Where(c => c.Owner != null)
                    .ToDictionary(c => c.UnitId, c => c.Owner.Id),
                OwnerIsHost = Game.Characters.Where(c => c.Owner != null)
                    .ToDictionary(c => c.UnitId, c => c.Owner.IsHost),
                NetworkCombatJson = JsonConvert.SerializeObject(Game.Combat),
                MechanicalState = mechanical, NativeSupplement = supplement,
                Seeds = CaptureCombatSaveSeeds()
            };
            // Validate the exact payload before the native ZIP is enriched. A
            // failed capture only leaves an ordinary native save; it must never
            // publish a manifest which the restoration path cannot consume.
            _ = CombatSaveNetworkState.Read(manifest);
            _ = CombatSaveNativeSupplement.Read(native);
            Logger.LogInformation("[CombatSave] Native host save captured for combat restoration. Unit={Unit}, Round={Round}",
                manifest.Unit, manifest.Round);
            return manifest;
        }
    }
}



