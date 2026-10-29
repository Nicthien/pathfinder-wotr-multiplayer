using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.View;
using Kingmaker.View.MapObjects;
using Kingmaker.Visual.Particles;
using Kingmaker.Visual.Particles.FxSpawnSystem;
using UnityEngine;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Called only after a verified transaction commits. Never invokes native
    // entity disposal, area destruction notifications, HandleEnd or UnitExit.
    public sealed class RealTimeNativeAreaVisualRetirement : IDisposable
    {
        private static FieldInfo Field(Type type, string name) => AccessTools.Field(type, name)
            ?? throw new MissingFieldException(type.FullName, name);
        private readonly AreaEffectEntityData _area;
        private readonly AreaEffectView _view;
        private bool _disposed;

        public RealTimeNativeAreaVisualRetirement(AreaEffectEntityData area)
        {
            _area = area ?? throw new ArgumentNullException(nameof(area));
            _view = area.View;
            ValidateForCommit();
        }

        public void ValidateForCommit()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RealTimeNativeAreaVisualRetirement));
            if (!ReferenceEquals(_area.View, _view)
                || !ReferenceEquals(_view, null) && (_view.GetType() != typeof(AreaEffectView)
                    || RealTimeAreaVisual.IsOwned(_view)
                    || !ReferenceEquals(Field(typeof(EntityViewBase), "m_Data").GetValue(_view), _area)))
                throw new InvalidOperationException("native-area-visual-owner-changed");
        }

        public void Dispose()
        {
            if (_disposed) return;
            ValidateForCommit();
            if (ReferenceEquals(_view, null)) { _disposed = true; return; }
            DisposeUnityView();
        }

        // Keep Unity internal calls out of the worldless managed cleanup path.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void DisposeUnityView()
        {
            List<Exception> failures = null;
            // This native callback only unsubscribes the view, removes its
            // navigation cut and disables its late-update driver (audited IL).
            try { AccessTools.Method(typeof(AreaEffectView), "OnDisable").Invoke(_view, null); }
            catch (Exception failure) { (failures ??= new()).Add(failure); }
            try
            {
                var fx = (IFxHandle)Field(typeof(AreaEffectView), "m_SpawnedFx").GetValue(_view);
                if (fx != null) FxHelper.Destroy(fx);
                Field(typeof(AreaEffectView), "m_SpawnedFx").SetValue(_view, null);
            }
            catch (Exception failure) { (failures ??= new()).Add(failure); }
            // Deferred OnDestroy must not enter world-dependent native code.
            RealTimeAreaVisual.MarkOwned(_view);
            Field(typeof(EntityDataBase), "<View>k__BackingField").SetValue(_area, null);
            Field(typeof(EntityViewBase), "m_Data").SetValue(_view, null);
            try { if (_view != null) UnityEngine.Object.Destroy(_view.gameObject); }
            catch (Exception failure) { (failures ??= new()).Add(failure); }
            _disposed = true;
            if (failures != null) throw new AggregateException("native-area-visual-retirement-failed", failures);
        }
    }
}
