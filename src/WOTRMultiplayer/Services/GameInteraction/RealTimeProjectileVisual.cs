using System;
using System.Linq;
using Kingmaker.Blueprints;
using Kingmaker.View;
using UnityEngine;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Extensions;

namespace WOTRMultiplayer.Services.GameInteraction
{
    public sealed class RealTimeProjectileVisual : IRealTimeProjectileVisual
    {
        private GameObject _root;
        private readonly Action<RealTimeProjectileVisual> _onDispose;
        private NetworkProjectileState _confirmed;
        private Vector3 _from;
        private Quaternion _rotationFrom;
        private DateTime _started;
        public bool IsAlive => _root != null || _confirmed?.IsHit == true;

        public static bool IsAllowedScript(Type type) => type?.FullName == "Kingmaker.View.ProjectileView"
            || type?.FullName == "Kingmaker.Visual.Particles.FxDestroyOnStop";

        public RealTimeProjectileVisual(NetworkProjectileState state, Action<RealTimeProjectileVisual> onDispose)
        {
            _onDispose = onDispose;
            var blueprint = ResourcesLibrary.TryGetBlueprint<BlueprintProjectile>(state.BlueprintId)
                ?? throw new InvalidOperationException("projectile-blueprint-missing:" + state.BlueprintId);
            var prefab = blueprint.View?.Load();
            if (prefab == null) throw new InvalidOperationException("projectile-prefab-missing:" + state.BlueprintId);
            if (prefab.GetComponentsInChildren<MonoBehaviour>(true).Any(script => script == null || !IsAllowedScript(script.GetType()))
                || prefab.GetComponentsInChildren<Animator>(true).Any()
                || prefab.GetComponentsInChildren<Animation>(true).Any()
                || prefab.GetComponentsInChildren<Collider>(true).Any()
                || prefab.GetComponentsInChildren<Rigidbody>(true).Any()
                || prefab.GetComponentsInChildren<ParticleSystem>(true).Any(particles =>
                    particles.collision.enabled || particles.trigger.enabled))
                throw new InvalidOperationException("projectile-prefab-unverified-scripts:" + state.BlueprintId);
            // Preflight precedes Instantiate/OnEnable. Never create a native
            // Projectile, register with its controller or attach an impact rule.
            _root = UnityEngine.Object.Instantiate(prefab.gameObject, state.Position.ToUnityVector3(), Rotation(state));
            try { Confirm(state); }
            catch { Dispose(); throw; }
        }

        public void Confirm(NetworkProjectileState state)
        {
            if (_root == null)
            {
                if (state.IsHit) { _confirmed = state.Copy(); return; }
                throw new InvalidOperationException("projectile-view-lost:" + state.Id);
            }
            if (Same(_confirmed, state)) return;
            _from = _root.transform.position;
            _rotationFrom = _root.transform.rotation;
            _started = DateTime.UtcNow;
            if (state.IsHit && _confirmed?.IsHit != true)
            {
                _root.transform.SetPositionAndRotation(state.Position.ToUnityVector3(), Rotation(state));
                foreach (var particles in _root.GetComponentsInChildren<ParticleSystem>(true))
                    particles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                // Flight visuals stop here. No native OnHit, callback, EventBus,
                // physics collision, attack roll or damage handler is invoked.
            }
            _confirmed = state.Copy();
        }

        public void Tick()
        {
            if (_root == null || _confirmed == null || _confirmed.IsHit) return;
            var progress = RealTimeVisualInterpolation.Progress(_started, DateTime.UtcNow, TimeSpan.FromMilliseconds(100));
            _root.transform.SetPositionAndRotation(Vector3.LerpUnclamped(_from, _confirmed.Position.ToUnityVector3(), progress),
                Quaternion.SlerpUnclamped(_rotationFrom, Rotation(_confirmed), progress));
        }

        private static Quaternion Rotation(NetworkProjectileState state) =>
            new(state.RotationX, state.RotationY, state.RotationZ, state.RotationW);

        private static bool Same(NetworkProjectileState left, NetworkProjectileState right) => left != null
            && left.Position.X == right.Position.X && left.Position.Y == right.Position.Y && left.Position.Z == right.Position.Z
            && left.RotationX == right.RotationX && left.RotationY == right.RotationY
            && left.RotationZ == right.RotationZ && left.RotationW == right.RotationW && left.IsHit == right.IsHit;

        public void Dispose()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _confirmed = null;
            _onDispose?.Invoke(this);
        }
    }
}
