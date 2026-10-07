using System;
using System.Runtime.CompilerServices;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Config.Mapping;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Services.GameInteraction
{
    public static class RealTimeUnitResultCapture
    {
        private sealed class Stamp { public ulong Hash; public bool Published; public WeakReference Encounter; }
        private static readonly ConditionalWeakTable<UnitEntityData, Stamp> Stamps = new();

        public static void Publish(UnitEntityData unit, string reason)
        {
            if (!RealTimePresentationRuntime.Enabled || unit == null
                || Main.Multiplayer?.IsRealTimeCombatActive != true || Main.Multiplayer.IsRealTimeClientCombatActive
                || Main.Multiplayer.IsRealTimeCombatBarrierActive) return;
            try
            {
                var encounter = Main.Multiplayer.RealTimeCombatIdentity;
                if (encounter == null) throw new InvalidOperationException("missing-result-encounter");
                RealTimeEncounterParticipants<UnitEntityData>.For(encounter).Remember(unit.UniqueId, unit);
                var state = RealTimeUnitResultReplication.Capture(unit);
                var hash = RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot
                {
                    Combat = new NetworkCombatState { Units = [new() { Id = unit.UniqueId, Descriptor = state }] }
                });
                var stamp = Stamps.GetValue(unit, _ => new Stamp());
                if (stamp.Published && ReferenceEquals(stamp.Encounter?.Target, encounter) && stamp.Hash == hash) return;
                Main.Multiplayer.PublishRealTimeAuthoritativeEvent(new NotifyRealTimeAuthoritativeEvent
                {
                    Kind = (int)RealTimeAuthoritativeEventKind.UnitResult,
                    ActionId = "result:" + Guid.NewGuid().ToString("N"), UnitId = unit.UniqueId,
                    HostGameTimeTicks = Game.Instance.TimeController.GameTime.Ticks,
                    UnitResult = new RealTimeUnitResultPayload
                    {
                        State = Main.Mapper.Map<Networking.Messages.Contracts.NetworkUnitDescriptor>(state),
                        Reason = reason
                    }
                });
                stamp.Hash = hash; stamp.Published = true; stamp.Encounter = new WeakReference(encounter);
            }
            catch (Exception failure)
            {
                Main.GetLogger<Stamp>().LogError(failure, "Unable to capture authoritative unit result. UnitId={UnitId}, Reason={Reason}", unit.UniqueId, reason);
                Main.Multiplayer.TogglePause(true);
                throw;
            }
        }
    }
}
