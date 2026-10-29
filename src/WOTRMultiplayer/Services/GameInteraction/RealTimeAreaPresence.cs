using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // A host-listed replica must be discoverable regardless of local culling.
    // Never invoke setters: they call OnSuppressedChanged/OnIsInGameChanged.
    public sealed class RealTimeAreaPresence : IDisposable
    {
        private static readonly FieldInfo InGame = AccessTools.Field(typeof(EntityDataBase), "m_IsInGame")
            ?? throw new MissingFieldException(typeof(EntityDataBase).FullName, "m_IsInGame");
        private static readonly FieldInfo Suppressed = AccessTools.Field(typeof(EntityDataBase), "m_Suppressed")
            ?? throw new MissingFieldException(typeof(EntityDataBase).FullName, "m_Suppressed");
        private readonly AreaEffectEntityData _area;
        private readonly bool _wasInGame;
        private readonly bool _wasSuppressed;
        private bool _committed;
        private bool _disposed;

        public static bool NeedsRestore(AreaEffectEntityData area) => RealTimeAreaReplicaFactory.IsReplica(area)
            && (!area.IsInGame || area.Suppressed);

        public RealTimeAreaPresence(AreaEffectEntityData area)
        {
            if (!NeedsRestore(area)) throw new InvalidOperationException("invalid-area-presence-restoration");
            _area = area; _wasInGame = area.IsInGame; _wasSuppressed = area.Suppressed;
            InGame.SetValue(area, true); Suppressed.SetValue(area, false);
        }

        public void ValidateForCommit()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RealTimeAreaPresence));
            if (!_area.IsInGame || _area.Suppressed)
                throw new InvalidOperationException("area-presence-ownership-changed");
        }

        public void Commit() { ValidateForCommit(); _committed = true; }
        public void Dispose()
        {
            if (_disposed) return;
            if (!_committed)
            {
                ValidateForCommit();
                InGame.SetValue(_area, _wasInGame); Suppressed.SetValue(_area, _wasSuppressed);
            }
            _disposed = true;
        }
    }
}
