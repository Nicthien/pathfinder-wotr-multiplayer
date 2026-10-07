using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // EntityPool is NOT EntityService's cache. Its enumerator additionally
    // filters m_IsInGame. Update both explicitly without Scene.OnAdded/Removed.
    public sealed class RealTimeAreaPoolMembership : IDisposable
    {
        private static readonly FieldInfo IsInGame = AccessTools.Field(typeof(EntityDataBase), "m_IsInGame")
            ?? throw new MissingFieldException(typeof(EntityDataBase).FullName, "m_IsInGame");
        private readonly IList _pool;
        private readonly AreaEffectEntityData _area;
        private readonly bool _removed;
        private readonly bool _previousInGame;
        private readonly int _index;
        private bool _committed;
        private bool _disposed;

        public RealTimeAreaPoolMembership(IList pool, AreaEffectEntityData area, bool removed)
        {
            if (pool == null || !RealTimeAreaReplicaFactory.IsReplica(area))
                throw new InvalidOperationException("invalid-area-pool-membership");
            _pool = pool; _area = area; _removed = removed;
            _previousInGame = area.IsInGame;
            _index = pool.IndexOf(area);
            var matches = pool.Cast<EntityDataBase>().Count(value => Same(value.UniqueId, area.UniqueId));
            if (removed ? _index < 0 || matches != 1 : matches != 0)
                throw new InvalidOperationException("area-pool-membership-conflict");
            if (removed) pool.RemoveAt(_index); else pool.Add(area);
            IsInGame.SetValue(area, !removed);
        }

        private static bool Same(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

        public void ValidateForCommit()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RealTimeAreaPoolMembership));
            if (_area.IsInGame == _removed || (_removed ? _pool.Contains(_area)
                : !_pool.Contains(_area) || _pool.Cast<EntityDataBase>().Count(value => Same(value.UniqueId, _area.UniqueId)) != 1))
                throw new InvalidOperationException("area-pool-membership-ownership-changed");
        }

        public void Commit() { ValidateForCommit(); _committed = true; }

        public void Dispose()
        {
            if (_disposed) return;
            if (!_committed)
            {
                ValidateForCommit();
                if (_removed)
                {
                    if (_pool.Cast<EntityDataBase>().Any(value => Same(value.UniqueId, _area.UniqueId)))
                        throw new InvalidOperationException("area-pool-rollback-conflict");
                    _pool.Insert(Math.Min(_index, _pool.Count), _area);
                }
                else _pool.Remove(_area);
                IsInGame.SetValue(_area, _previousInGame);
            }
            _disposed = true;
        }
    }
}
