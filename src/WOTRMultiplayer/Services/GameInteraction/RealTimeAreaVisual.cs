using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.Utility;
using Kingmaker.View;
using Kingmaker.View.MapObjects;
using Kingmaker.Visual.Particles;
using Kingmaker.Visual.Particles.FxSpawnSystem;
using UnityEngine;
using WOTRMultiplayer.Entities.AreaEffects;
using WOTRMultiplayer.Extensions;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Explicit presentation ownership: never AttachView/CreateEntityData, native
    // OnEnable (pit navigation), or native OnDestroy (world-dependent cleanup).
    public sealed class RealTimeAreaVisual : IDisposable
    {
        private sealed class Ownership { public RealTimeAreaVisual Visual; }
        private static readonly ConditionalWeakTable<AreaEffectView, Ownership> Owned = new();
        private static FieldInfo Field(Type type, string name) => AccessTools.Field(type, name)
            ?? throw new MissingFieldException(type.FullName, name);
        private readonly AreaEffectEntityData _area;
        private AreaEffectView _view;
        private GameObject _root;
        private IFxHandle _fx;

        public static bool IsOwned(AreaEffectView view) => !ReferenceEquals(view, null)
            && Owned.TryGetValue(view, out _);

        // Separately callable without Unity initialization for lifecycle probes.
        internal static void MarkOwned(AreaEffectView view) => Owned.Add(view, new Ownership());
        public static RealTimeAreaVisual For(AreaEffectView view) => !ReferenceEquals(view, null)
            && Owned.TryGetValue(view, out var owner) ? owner.Visual : null;

        public RealTimeAreaVisual(AreaEffectEntityData area, NetworkAreaEffect confirmed)
        {
            if (!RealTimePresentationRuntime.Enabled || !RealTimeAreaReplicaFactory.IsReplica(area)
                || !ReferenceEquals(area.View, null))
                throw new InvalidOperationException("invalid-area-visual-owner");
            var error = RealTimeAreaReplication.Validate(area, confirmed);
            if (error != null) throw new InvalidOperationException("area-visual:" + error);
            if (!SupportsShape(area.Blueprint.Shape))
                throw new InvalidOperationException("area-visual-unsupported-shape:" + area.Blueprint.Shape);
            var prefab = area.Blueprint.Fx?.Load();
            if (prefab != null && (prefab.GetComponentsInChildren<MonoBehaviour>(true)
                    .Any(script => script == null || !IsAllowedScript(script.GetType()))
                || prefab.GetComponentsInChildren<Animator>(true).Any()
                || prefab.GetComponentsInChildren<Animation>(true).Any()
                || prefab.GetComponentsInChildren<Collider>(true).Any()
                || prefab.GetComponentsInChildren<Rigidbody>(true).Any()
                || prefab.GetComponentsInChildren<ParticleSystem>(true)
                    .Any(particles => particles.collision.enabled || particles.trigger.enabled)))
                throw new InvalidOperationException("area-prefab-unverified-scripts:" + confirmed.BlueprintId);
            _area = area;
            try
            {
                _root = new GameObject("RTwP confirmed area " + area.UniqueId);
                _root.SetActive(false);
                _view = _root.AddComponent<AreaEffectView>();
                MarkOwned(_view); // Before activation; Awake only caches the shape.
                Owned.GetValue(_view, _ => new Ownership()).Visual = this;
                _view.OnUnit = confirmed.IsAttachedToUnit;
                _view.InitAtRuntime(area.Context, area.Blueprint,
                    new TargetWrapper(confirmed.Position.ToUnityVector3(), confirmed.Orientation),
                    TimeSpan.FromTicks(confirmed.CreationGameTimeTicks),
                    confirmed.DurationTicks.HasValue ? TimeSpan.FromTicks(confirmed.DurationTicks.Value) : null);
                // Raw binding avoids collider links, fact activation, subscriptions
                // and the native OnViewDidAttach -> SpawnFxs callback chain.
                Field(typeof(EntityViewBase), "m_Data").SetValue(_view, area);
                Field(typeof(EntityDataBase), "<View>k__BackingField").SetValue(area, _view);
                _view.UniqueId = area.UniqueId;
                _view.Transform = _root.transform;
                Confirm(confirmed);
                _root.SetActive(true);
                if (prefab != null)
                    _fx = FxHelper.SpawnFxOnGameObject(prefab, _root, false);
                Confirm(confirmed);
            }
            catch { Dispose(); throw; }
        }

        public static bool SupportsShape(AreaEffectShape shape) => shape == AreaEffectShape.Cylinder
            || shape == AreaEffectShape.Wall || shape == AreaEffectShape.AllArea;

        public static bool IsAllowedScript(Type type) =>
            type?.FullName == "Kingmaker.Visual.Particles.FxDestroyOnStop";

        public void Confirm(NetworkAreaEffect confirmed)
        {
            if (_root == null || confirmed == null || confirmed.Id != _area.UniqueId)
                throw new InvalidOperationException("area-visual-lost-or-mismatched");
            _root.transform.SetPositionAndRotation(confirmed.Position.ToUnityVector3(),
                Quaternion.Euler(0, confirmed.Orientation, 0));
            // The native FX system can detach an instance from its locator.
            // Follow only confirmed transforms, never a client target calculation.
            if (_fx?.IsSpawned == true && _fx.SpawnedObject != null)
                _fx.SpawnedObject.transform.SetPositionAndRotation(_root.transform.position, _root.transform.rotation);
        }

        public void Dispose()
        {
            var root = _root;
            _root = null;
            try
            {
                if (_fx != null) FxHelper.Destroy(_fx);
            }
            finally
            {
                _fx = null;
                if (!ReferenceEquals(_view, null))
                {
                    if (ReferenceEquals(_area.View, _view))
                        Field(typeof(EntityDataBase), "<View>k__BackingField").SetValue(_area, null);
                    Field(typeof(EntityViewBase), "m_Data").SetValue(_view, null);
                    _view = null;
                }
                // Keep the weak ownership marker until Unity's deferred Destroy
                // callbacks have run, including after a disconnect.
                if (root != null) UnityEngine.Object.Destroy(root);
            }
        }
    }
}
