using System;
using System.Linq;
using System.Collections.Generic;
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
        private static readonly ConditionalWeakTable<AreaEffectEntityData, HashSet<RealTimeAreaVisual>> AreaVisuals = new();
        private static FieldInfo Field(Type type, string name) => AccessTools.Field(type, name)
            ?? throw new MissingFieldException(type.FullName, name);
        private readonly AreaEffectEntityData _area;
        private AreaEffectView _view;
        private GameObject _root;
        private IFxHandle _fx;
        private GameObject _prefab;
        private bool _shown;
        public AreaEffectView View => _view;
        public bool IsAlive => _root != null && _view != null;

        public static bool IsOwned(AreaEffectView view) => !ReferenceEquals(view, null)
            && Owned.TryGetValue(view, out _);

        // Separately callable without Unity initialization for lifecycle probes.
        internal static void MarkOwned(AreaEffectView view) => Owned.Add(view, new Ownership());
        public static RealTimeAreaVisual For(AreaEffectView view) => !ReferenceEquals(view, null)
            && Owned.TryGetValue(view, out var owner) ? owner.Visual : null;
        public static IReadOnlyList<RealTimeAreaVisual> ForArea(AreaEffectEntityData area) =>
            area != null && AreaVisuals.TryGetValue(area, out var visuals) ? visuals.ToArray() : Array.Empty<RealTimeAreaVisual>();

        public RealTimeAreaVisual(AreaEffectEntityData area, NetworkAreaEffect confirmed)
        {
            if (!RealTimePresentationRuntime.Enabled || !RealTimeAreaReplicaFactory.IsReplica(area))
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
                // Data binding belongs to the restore transaction. Keep the old
                // view intact until all replacement assets have been prepared.
                _view.UniqueId = area.UniqueId;
                _view.Transform = _root.transform;
                Confirm(confirmed);
                _prefab = prefab;
                AreaVisuals.GetValue(area, _ => new HashSet<RealTimeAreaVisual>()).Add(this);
            }
            catch { Dispose(); throw; }
        }

        public static bool SupportsShape(AreaEffectShape shape) => shape == AreaEffectShape.Cylinder
            || shape == AreaEffectShape.Wall || shape == AreaEffectShape.AllArea;

        public static bool IsAllowedScript(Type type) =>
            type?.FullName == "Kingmaker.Visual.Particles.FxDestroyOnStop";

        public void Show(NetworkAreaEffect confirmed)
        {
            if (!IsAlive || !ReferenceEquals(_area.View, _view))
                throw new InvalidOperationException("area-visual-not-bound");
            _root.SetActive(true);
            if (!_shown)
            {
                _shown = true;
                if (_prefab != null) _fx = FxHelper.SpawnFxOnGameObject(_prefab, _root, false);
            }
            Confirm(confirmed);
        }

        public void Confirm(NetworkAreaEffect confirmed)
        {
            if (_root == null || confirmed == null || confirmed.Id != _area.UniqueId)
                throw new InvalidOperationException("area-visual-lost-or-mismatched");
            _root.transform.SetPositionAndRotation(confirmed.Position.ToUnityVector3(),
                Quaternion.Euler(0, confirmed.Orientation, 0));
            if (_shown && !_root.activeSelf) _root.SetActive(true);
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
                if (AreaVisuals.TryGetValue(_area, out var visuals)) visuals.Remove(this);
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
