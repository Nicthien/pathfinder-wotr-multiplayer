using System;
using System.Collections;
using System.Collections.Generic;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Buffs;
using Kingmaker.View.MapObjects;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Rolls back new presentation facts, not arbitrary pre-existing world state.
    // A failed checkpoint must therefore remain protected after disposal.
    public sealed class RealTimeBuffRestoreTransaction : IDisposable
    {
        private readonly List<RealTimeBuffReplicaStore.Registration> _registrations = new();
        private readonly List<RealTimeBuffReplicaStore.Removal> _removals = new();
        private readonly List<RealTimeAreaReplicaStore.Registration> _areaRegistrations = new();
        private readonly List<RealTimeAreaReplicaStore.Removal> _areaRemovals = new();
        private readonly HashSet<string> _areaRegistrationsById = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _areaRemovalsById = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Action> _refreshAfterRollback = new();
        private readonly List<IDisposable> _createdAreaVisuals = new();
        private readonly List<IDisposable> _removedAreaVisuals = new();
        private readonly List<RealTimeAreaPoolMembership> _areaPoolChanges = new();
        private readonly List<RealTimeAreaViewBinding> _areaViewBindings = new();
        private readonly List<RealTimeAreaPresence> _areaPresence = new();
        private bool _disposed;
        private bool _committed;
        public bool HasChanges => _registrations.Count != 0 || _removals.Count != 0
            || _areaRegistrations.Count != 0 || _areaRemovals.Count != 0
            || _createdAreaVisuals.Count != 0 || _removedAreaVisuals.Count != 0 || _areaPoolChanges.Count != 0
            || _areaViewBindings.Count != 0 || _areaPresence.Count != 0;

        public void BindAreaView(AreaEffectEntityData area, AreaEffectView view)
        {
            EnsureOpen();
            _areaViewBindings.Add(new RealTimeAreaViewBinding(area, view));
        }

        public void RestoreAreaPresence(AreaEffectEntityData area)
        {
            EnsureOpen();
            if (RealTimeAreaPresence.NeedsRestore(area)) _areaPresence.Add(new RealTimeAreaPresence(area));
        }

        public void TrackAreaPool(IList pool, AreaEffectEntityData area, bool removed)
        {
            EnsureOpen();
            _areaPoolChanges.Add(new RealTimeAreaPoolMembership(pool, area, removed));
        }

        public void TrackAreaVisual(IDisposable visual, bool removed)
        {
            EnsureOpen();
            if (visual == null) throw new ArgumentNullException(nameof(visual));
            if (_createdAreaVisuals.Contains(visual) || _removedAreaVisuals.Contains(visual))
                throw new InvalidOperationException("duplicate-area-visual-ownership");
            (removed ? _removedAreaVisuals : _createdAreaVisuals).Add(visual);
        }

        public void RegisterArea(EntityService service, SceneEntitiesState scene, AreaEffectEntityData area, string expectedSceneName)
        {
            EnsureOpen();
            if (area == null || !_areaRegistrationsById.Add(area.UniqueId))
                throw new InvalidOperationException("duplicate-area-restoration-registration");
            RealTimeAreaReplicaStore.Registration registration;
            try { registration = RealTimeAreaReplicaStore.Register(service, scene, area, expectedSceneName); }
            catch { _areaRegistrationsById.Remove(area.UniqueId); throw; }
            _areaRegistrations.Add(registration);
            foreach (var removal in _areaRemovals) removal.TrackReplacement(registration);
        }

        public void RemoveArea(EntityService service, SceneEntitiesState scene, AreaEffectEntityData area)
        {
            EnsureOpen();
            if (area == null || !_areaRemovalsById.Add(area.UniqueId))
                throw new InvalidOperationException("duplicate-area-restoration-removal");
            try { _areaRemovals.Add(RealTimeAreaReplicaStore.RemoveReplica(service, scene, area)); }
            catch { _areaRemovalsById.Remove(area.UniqueId); throw; }
        }

        public void Remove(EntityFactsManager manager, Buff buff, Action refreshAfterRollback)
        {
            EnsureOpen();
            _removals.Add(RealTimeBuffReplicaStore.RemoveReplica(manager, buff));
            if (refreshAfterRollback != null) _refreshAfterRollback.Add(refreshAfterRollback);
        }

        public void Register(EntityFactsManager manager, Buff buff, Action refreshAfterRollback)
        {
            EnsureOpen();
            var registration = RealTimeBuffReplicaStore.Register(manager, buff);
            _registrations.Add(registration);
            if (refreshAfterRollback != null) _refreshAfterRollback.Add(refreshAfterRollback);
        }

        public void VerifyAndCommit(NetworkCombatState expected, NetworkCombatState actual)
        {
            EnsureOpen();
            if (HasChanges)
            {
                if (RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = expected })
                    != RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = actual }))
                    throw new InvalidOperationException("buff-restoration-checkpoint-hash-mismatch");
                var mismatch = RealTimeCombatTemporalDiff.Summarize(expected, actual);
                if (mismatch != null) throw new InvalidOperationException("buff-restoration:" + mismatch);
            }
            foreach (var registration in _areaRegistrations) registration.ValidateForCommit();
            foreach (var removal in _areaRemovals) removal.ValidateForCommit();
            foreach (var change in _areaPoolChanges) change.ValidateForCommit();
            foreach (var binding in _areaViewBindings) binding.ValidateForCommit();
            foreach (var presence in _areaPresence) presence.ValidateForCommit();
            foreach (var registration in _registrations) registration.Commit();
            foreach (var removal in _removals) removal.Commit();
            foreach (var registration in _areaRegistrations) registration.Commit();
            foreach (var removal in _areaRemovals) removal.Commit();
            foreach (var change in _areaPoolChanges) change.Commit();
            foreach (var binding in _areaViewBindings) binding.Commit();
            foreach (var presence in _areaPresence) presence.Commit();
            _committed = true;
        }

        private void EnsureOpen()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RealTimeBuffRestoreTransaction));
            if (_committed) throw new InvalidOperationException("buff-restoration-already-committed");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            List<Exception> failures = null;
            for (var i = _areaViewBindings.Count - 1; i >= 0; i--)
            {
                try { _areaViewBindings[i].Dispose(); }
                catch (Exception failure) { (failures ??= new()).Add(failure); }
            }
            for (var i = _areaPresence.Count - 1; i >= 0; i--)
            {
                try { _areaPresence[i].Dispose(); }
                catch (Exception failure) { (failures ??= new()).Add(failure); }
            }
            for (var i = _areaPoolChanges.Count - 1; i >= 0; i--)
            {
                try { _areaPoolChanges[i].Dispose(); }
                catch (Exception failure) { (failures ??= new()).Add(failure); }
            }
            for (var i = _areaRegistrations.Count - 1; i >= 0; i--)
            {
                try { _areaRegistrations[i].Dispose(); }
                catch (Exception failure) { (failures ??= new()).Add(failure); }
            }
            for (var i = _areaRemovals.Count - 1; i >= 0; i--)
            {
                try { _areaRemovals[i].Dispose(); }
                catch (Exception failure) { (failures ??= new()).Add(failure); }
            }
            for (var i = _registrations.Count - 1; i >= 0; i--)
            {
                try { _registrations[i].Dispose(); }
                catch (Exception failure) { (failures ??= new()).Add(failure); }
            }
            for (var i = _removals.Count - 1; i >= 0; i--)
            {
                try { _removals[i].Dispose(); }
                catch (Exception failure) { (failures ??= new()).Add(failure); }
            }
            var visuals = _committed ? _removedAreaVisuals : _createdAreaVisuals;
            for (var i = visuals.Count - 1; i >= 0; i--)
            {
                try { visuals[i].Dispose(); }
                catch (Exception failure) { (failures ??= new()).Add(failure); }
            }
            if (_committed)
            {
                if (failures != null) throw new AggregateException("area-restoration-commit-cleanup-failed", failures);
                return;
            }
            // Restore scheduler references only after all new facts are removed.
            foreach (var refresh in _refreshAfterRollback)
            {
                try { refresh(); }
                catch (Exception failure) { (failures ??= new()).Add(failure); }
            }
            if (failures != null) throw new AggregateException("buff-restoration-scheduler-rollback-failed", failures);
        }
    }
}
