using System;
using System.Linq;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Raw bookkeeping only. Never SceneEntitiesState.Add/RemoveEntityData,
    // EntityService.Register/Unregister, Dispose(entity), or lifecycle events.
    public static class RealTimeAreaReplicaStore
    {
        public static Registration Register(EntityService service, SceneEntitiesState scene,
            AreaEffectEntityData area, string expectedSceneName)
        {
            if (service == null || scene == null || !RealTimeAreaReplicaFactory.IsReplica(area)
                || string.IsNullOrWhiteSpace(expectedSceneName) || scene.SceneName != expectedSceneName
                || area.HoldingState != null || area.Proxy != null)
                throw new InvalidOperationException("invalid-area-replica-registration");
            lock (service.m_EntitiesLock)
            {
                if (scene.AllEntityData.Any(entity => Same(entity.UniqueId, area.UniqueId))
                    || service.m_Entities.Any(entry => Same(entry.Key, area.UniqueId) && entry.Key != area.UniqueId))
                    throw new InvalidOperationException("duplicate-area-replica-registration");
                var exists = service.m_Entities.TryGetValue(area.UniqueId, out var proxy);
                if (exists && (proxy.Id != area.UniqueId || proxy.IsDisposed || proxy.Entity != null))
                    throw new InvalidOperationException("occupied-area-replica-proxy");
                proxy ??= new EntityService.Proxy(area.UniqueId);
                if (!exists) service.m_Entities.Add(area.UniqueId, proxy);
                try
                {
                    proxy.Entity = area;
                    area.Proxy = proxy;
                    area.HoldingState = scene;
                    scene.AllEntityData.Add(area);
                    service.m_Cache.Add(area);
                    return new Registration(service, scene, area, proxy, !exists);
                }
                catch
                {
                    service.m_Cache.Remove(area);
                    scene.AllEntityData.Remove(area);
                    area.HoldingState = null; area.Proxy = null; proxy.Entity = null;
                    if (!exists)
                    {
                        service.m_Entities.Remove(area.UniqueId);
                        proxy.Dispose();
                    }
                    throw;
                }
            }
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        public sealed class Registration : IDisposable
        {
            internal EntityService _service;
            internal readonly SceneEntitiesState _scene;
            internal readonly AreaEffectEntityData _area;
            internal readonly EntityService.Proxy _proxy;
            private readonly bool _createdProxy;
            private bool _committed;
            internal Registration(EntityService service, SceneEntitiesState scene, AreaEffectEntityData area,
                EntityService.Proxy proxy, bool createdProxy)
            { _service = service; _scene = scene; _area = area; _proxy = proxy; _createdProxy = createdProxy; }
            public void ValidateForCommit()
            {
                if (_service == null) throw new ObjectDisposedException(nameof(Registration));
                lock (_service.m_EntitiesLock)
                    if (!_service.m_Entities.TryGetValue(_area.UniqueId, out var current) || !ReferenceEquals(current, _proxy)
                        || _proxy.IsDisposed || !ReferenceEquals(_proxy.Entity, _area) || !ReferenceEquals(_area.Proxy, _proxy)
                        || !ReferenceEquals(_area.HoldingState, _scene)
                        || !_scene.AllEntityData.Contains(_area)
                        || _scene.AllEntityData.Count(entity => Same(entity.UniqueId, _area.UniqueId)) != 1)
                        throw new InvalidOperationException("area-registration-ownership-changed");
            }
            public void Commit() { ValidateForCommit(); _committed = true; }
            public void Dispose()
            {
                if (_service == null) return;
                if (_committed) { _service = null; return; }
                ValidateForCommit();
                var service = _service;
                lock (service.m_EntitiesLock)
                {
                    service.m_Cache.Remove(_area);
                    _scene.AllEntityData.Remove(_area);
                    _area.HoldingState = null; _area.Proxy = null; _proxy.Entity = null;
                    if (_createdProxy)
                    {
                        service.m_Entities.Remove(_area.UniqueId);
                        _proxy.Dispose();
                    }
                    _service = null;
                }
            }
        }

        public static Removal RemoveReplica(EntityService service, SceneEntitiesState scene, AreaEffectEntityData area)
        {
            if (service == null || scene == null || !RealTimeAreaReplicaFactory.IsReplica(area))
                throw new InvalidOperationException("invalid-area-replica-removal");
            lock (service.m_EntitiesLock)
            {
                var index = scene.AllEntityData.IndexOf(area);
                if (index < 0 || !ReferenceEquals(area.HoldingState, scene))
                    throw new InvalidOperationException("unregistered-area-replica-removal");
                var registered = service.m_Entities.TryGetValue(area.UniqueId, out var proxy);
                if (registered && proxy.Entity != null && !ReferenceEquals(proxy.Entity, area))
                    throw new InvalidOperationException("area-proxy-owned-by-other-entity");
                var valid = registered && !proxy.IsDisposed && ReferenceEquals(proxy.Entity, area)
                    && ReferenceEquals(area.Proxy, proxy);
                var replacedProxy = valid ? null : proxy;
                var originalAreaProxy = area.Proxy;
                var originalProxyEntity = proxy?.Entity;
                if (!valid)
                {
                    proxy = new EntityService.Proxy(area.UniqueId);
                    service.m_Entities[area.UniqueId] = proxy;
                }
                service.m_Cache.Remove(area);
                scene.AllEntityData.RemoveAt(index);
                area.HoldingState = null; area.Proxy = null; proxy.Entity = null;
                // Keep or recreate the unresolved proxy until verification/commit
                // so the replica can be restored by identity on rollback.
                return new Removal(service, scene, area, proxy, index,
                    replacedProxy, originalAreaProxy, originalProxyEntity);
            }
        }

        public sealed class Removal : IDisposable
        {
            private EntityService _service;
            private readonly SceneEntitiesState _scene;
            private readonly AreaEffectEntityData _area;
            private readonly EntityService.Proxy _proxy;
            private readonly EntityService.Proxy _replacedProxy;
            private readonly EntityService.Proxy _originalAreaProxy;
            private readonly EntityDataBase _originalProxyEntity;
            private readonly int _index;
            private bool _committed;
            private Registration _replacement;
            internal Removal(EntityService service, SceneEntitiesState scene, AreaEffectEntityData area,
                EntityService.Proxy proxy, int index, EntityService.Proxy replacedProxy,
                EntityService.Proxy originalAreaProxy, EntityDataBase originalProxyEntity)
            {
                _service = service; _scene = scene; _area = area; _proxy = proxy; _index = index;
                _replacedProxy = replacedProxy; _originalAreaProxy = originalAreaProxy;
                _originalProxyEntity = originalProxyEntity;
            }
            public void ValidateForCommit()
            {
                if (_service == null) throw new ObjectDisposedException(nameof(Removal));
                if (_replacement != null)
                {
                    _replacement.ValidateForCommit();
                    if (_area.Proxy != null || _area.HoldingState != null)
                        throw new InvalidOperationException("area-removal-ownership-changed");
                    return;
                }
                ValidatePendingRemoval();
            }
            private void ValidatePendingRemoval()
            {
                lock (_service.m_EntitiesLock)
                    if (!_service.m_Entities.TryGetValue(_area.UniqueId, out var current) || !ReferenceEquals(current, _proxy)
                        || _proxy.IsDisposed || _proxy.Entity != null || _area.Proxy != null || _area.HoldingState != null
                        || _scene.AllEntityData.Any(entity => Same(entity.UniqueId, _area.UniqueId)))
                        throw new InvalidOperationException("area-removal-ownership-changed");
            }
            internal void TrackReplacement(Registration registration)
            {
                if (ReferenceEquals(registration._service, _service) && ReferenceEquals(registration._proxy, _proxy))
                {
                    if (_replacement != null) throw new InvalidOperationException("duplicate-area-replacement");
                    _replacement = registration;
                }
            }
            public void Commit()
            {
                ValidateForCommit();
                lock (_service.m_EntitiesLock)
                {
                    if (_replacement == null)
                    {
                        _service.m_Entities.Remove(_area.UniqueId);
                        _proxy.Dispose(); // Only sets IsDisposed and Entity; no entity callback.
                    }
                    _committed = true;
                }
            }
            public void Dispose()
            {
                if (_service == null) return;
                if (_committed) { _service = null; return; }
                ValidatePendingRemoval();
                lock (_service.m_EntitiesLock)
                {
                    var restoredProxy = _replacedProxy ?? _proxy;
                    _service.m_Entities[_area.UniqueId] = restoredProxy;
                    restoredProxy.Entity = _originalProxyEntity;
                    _area.Proxy = _originalAreaProxy; _area.HoldingState = _scene;
                    _scene.AllEntityData.Insert(Math.Min(_index, _scene.AllEntityData.Count), _area);
                    _service.m_Cache.Add(_area);
                    _service = null;
                }
            }
        }
    }
}
