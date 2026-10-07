using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.View;
using Kingmaker.View.MapObjects;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Binding only, no Unity operations. The transaction separately owns the
    // lifetime of old/new renderers and destroys only the appropriate one.
    public sealed class RealTimeAreaViewBinding : IDisposable
    {
        private static readonly FieldInfo Data = AccessTools.Field(typeof(EntityViewBase), "m_Data")
            ?? throw new MissingFieldException(typeof(EntityViewBase).FullName, "m_Data");
        private static readonly FieldInfo View = AccessTools.Field(typeof(EntityDataBase), "<View>k__BackingField")
            ?? throw new MissingFieldException(typeof(EntityDataBase).FullName, "<View>k__BackingField");
        private readonly AreaEffectEntityData _area;
        private readonly AreaEffectView _view;
        private readonly AreaEffectView _previous;
        private bool _committed;
        private bool _disposed;

        public RealTimeAreaViewBinding(AreaEffectEntityData area, AreaEffectView view)
        {
            if (!RealTimeAreaReplicaFactory.IsReplica(area) || !RealTimeAreaVisual.IsOwned(view)
                || Data.GetValue(view) != null)
                throw new InvalidOperationException("invalid-area-view-binding");
            _area = area; _view = view; _previous = area.View;
            if (!ReferenceEquals(_previous, null) && !RealTimeAreaVisual.IsOwned(_previous))
                throw new InvalidOperationException("area-view-binding-unowned-previous-view");
            if (!ReferenceEquals(_previous, null) && Data.GetValue(_previous) is { } previousData
                && !ReferenceEquals(previousData, area))
                throw new InvalidOperationException("area-view-binding-previous-owner-changed");
            Data.SetValue(view, area);
            View.SetValue(area, view);
        }

        public void ValidateForCommit()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RealTimeAreaViewBinding));
            if (!ReferenceEquals(_area.View, _view) || !ReferenceEquals(Data.GetValue(_view), _area))
                throw new InvalidOperationException("area-view-binding-ownership-changed");
        }

        public void Commit() { ValidateForCommit(); _committed = true; }

        public void Dispose()
        {
            if (_disposed) return;
            if (!_committed)
            {
                ValidateForCommit();
                View.SetValue(_area, _previous);
                Data.SetValue(_view, null);
            }
            _disposed = true;
        }
    }
}
