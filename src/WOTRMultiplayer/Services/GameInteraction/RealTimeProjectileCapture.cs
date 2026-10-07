using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Kingmaker;
using Kingmaker.Controllers.Projectiles;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Services.GameInteraction
{
    public static class RealTimeProjectileCapture
    {
        private sealed class Stamp
        {
            public readonly string Id = Guid.NewGuid().ToString("N");
            public DateTime NextUpdate;
            public bool Published;
            public bool HitPublished;
        }
        private static readonly ConditionalWeakTable<Projectile, Stamp> Stamps = new();

        public static List<NetworkProjectileState> CaptureAll() => Game.Instance.ProjectileController.Projectiles
            .Select(Capture).Where(state => state != null).ToList();

        public static NetworkProjectileState Capture(Projectile projectile)
        {
            if (projectile == null || projectile.Cleared || projectile.View == null || projectile.Blueprint == null) return null;
            var position = projectile.View.transform.position;
            var rotation = projectile.View.transform.rotation;
            return new NetworkProjectileState
            {
                Id = Stamps.GetValue(projectile, _ => new Stamp()).Id, BlueprintId = projectile.Blueprint.AssetGuid.ToString(),
                Position = new NetworkVector3(position.x, position.y, position.z),
                RotationX = rotation.x, RotationY = rotation.y, RotationZ = rotation.z, RotationW = rotation.w,
                IsHit = projectile.IsHit
            };
        }

        public static void Publish(Projectile projectile, bool impact)
        {
            if (!RealTimePresentationRuntime.Enabled || Main.Multiplayer?.IsRealTimeCombatActive != true
                || Main.Multiplayer.IsRealTimeClientCombatActive || Main.Multiplayer.IsRealTimeCombatBarrierActive) return;
            var state = Capture(projectile);
            if (state == null) return;
            var stamp = Stamps.GetValue(projectile, _ => new Stamp());
            impact |= state.IsHit;
            if (impact ? stamp.HitPublished : DateTime.UtcNow < stamp.NextUpdate) return;
            var stage = impact ? 3 : stamp.Published ? 2 : 1;
            stamp.Published = true;
            stamp.HitPublished |= impact;
            stamp.NextUpdate = DateTime.UtcNow.AddMilliseconds(100);
            state.IsHit = impact;
            Main.Multiplayer.PublishRealTimeAuthoritativeEvent(new NotifyRealTimeAuthoritativeEvent
            {
                Kind = (int)RealTimeAuthoritativeEventKind.ProjectilePresentation,
                ActionId = state.Id, UnitId = projectile.Launcher?.Unit?.UniqueId,
                HostGameTimeTicks = Game.Instance.TimeController.GameTime.Ticks,
                Projectile = new RealTimeProjectilePresentationPayload
                {
                    Stage = stage, State = new Networking.Messages.Contracts.NetworkProjectileState
                    {
                        Id = state.Id, BlueprintId = state.BlueprintId, IsHit = state.IsHit,
                        Position = new Networking.Messages.Contracts.NetworkVector3 { X = state.Position.X, Y = state.Position.Y, Z = state.Position.Z },
                        RotationX = state.RotationX, RotationY = state.RotationY, RotationZ = state.RotationZ, RotationW = state.RotationW
                    }
                }
            });
        }
    }
}
