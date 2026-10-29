using System;
using System.Collections.Generic;
using System.Linq;
using WOTRMultiplayer.Entities.Combat.RealTime;

namespace WOTRMultiplayer.Services
{
    public interface IRealTimeProjectileVisual : IDisposable
    {
        bool IsAlive { get; }
        void Confirm(NetworkProjectileState state);
    }

    // The replica owns confirmed data, never a native Projectile or an impact
    // callback. The renderer interpolates these confirmations independently.
    public sealed class RealTimeProjectileReplica
    {
        private readonly Func<NetworkProjectileState, IRealTimeProjectileVisual> _create;
        private Dictionary<string, (NetworkProjectileState State, IRealTimeProjectileVisual View)> _entries = new(StringComparer.Ordinal);
        public RealTimeProjectileReplica(Func<NetworkProjectileState, IRealTimeProjectileVisual> create) =>
            _create = create ?? throw new ArgumentNullException(nameof(create));

        public static bool IsValid(NetworkProjectileState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.Id) || state.Id.Length > 128
                || !Guid.TryParse(state.BlueprintId, out _) || state.Position == null
                || !Finite(state.Position.X) || !Finite(state.Position.Y) || !Finite(state.Position.Z)
                || !Finite(state.RotationX) || !Finite(state.RotationY)
                || !Finite(state.RotationZ) || !Finite(state.RotationW)) return false;
            var norm = (double)state.RotationX * state.RotationX + (double)state.RotationY * state.RotationY
                + (double)state.RotationZ * state.RotationZ + (double)state.RotationW * state.RotationW;
            return Math.Abs(norm - 1) < 0.01;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static List<NetworkProjectileState> ValidateAndCopy(IEnumerable<NetworkProjectileState> states)
        {
            if (states == null) throw new InvalidOperationException("projectile-state-missing");
            var result = new List<NetworkProjectileState>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var state in states)
            {
                if (!IsValid(state) || !ids.Add(state.Id) || result.Count >= 2048)
                    throw new InvalidOperationException("invalid-projectile-state");
                result.Add(state.Copy());
            }
            return result;
        }

        public List<NetworkProjectileState> Snapshot() => _entries.Values.Select(entry => entry.State.Copy()).ToList();

        public void Apply(NetworkProjectileState state)
        {
            if (!IsValid(state)) throw new InvalidOperationException("invalid-projectile-state");
            var copy = state.Copy();
            var existing = _entries.TryGetValue(copy.Id, out var old);
            if (existing)
            {
                if (old.State.BlueprintId != copy.BlueprintId || old.State.IsHit && !copy.IsHit)
                    throw new InvalidOperationException("conflicting-projectile-state");
                if (old.View.IsAlive)
                {
                    try { old.View.Confirm(copy.Copy()); }
                    catch (Exception cause)
                    {
                        try { old.View.Confirm(old.State.Copy()); }
                        catch (Exception rollback) { throw new AggregateException("projectile-update-rollback-failed", cause, rollback); }
                        throw;
                    }
                    _entries[copy.Id] = (copy, old.View);
                    return;
                }
            }
            else if (_entries.Count >= 2048) throw new InvalidOperationException("projectile-window-overflow");
            var view = _create(copy.Copy()) ?? throw new InvalidOperationException("projectile-view-not-created");
            try { view.Confirm(copy.Copy()); }
            catch (Exception cause)
            {
                try { view.Dispose(); }
                catch (Exception cleanup) { throw new AggregateException("projectile-update-cleanup-failed", cause, cleanup); }
                throw;
            }
            _entries[copy.Id] = (copy, view);
            if (existing) old.View.Dispose();
        }

        public void Restore(IEnumerable<NetworkProjectileState> states)
        {
            var copies = ValidateAndCopy(states);
            var next = new Dictionary<string, (NetworkProjectileState State, IRealTimeProjectileVisual View)>(StringComparer.Ordinal);
            var created = new List<IRealTimeProjectileVisual>();
            var changed = new List<(NetworkProjectileState State, IRealTimeProjectileVisual View)>();
            try
            {
                // Prepare every missing representation before changing existing ones.
                foreach (var state in copies)
                {
                    IRealTimeProjectileVisual view;
                    if (_entries.TryGetValue(state.Id, out var old) && old.State.BlueprintId == state.BlueprintId
                        && old.View.IsAlive && !(old.State.IsHit && !state.IsHit))
                        view = old.View;
                    else
                    {
                        view = _create(state.Copy()) ?? throw new InvalidOperationException("projectile-view-not-created");
                        created.Add(view);
                    }
                    next.Add(state.Id, (state, view));
                }
                foreach (var entry in next.Values)
                {
                    if (_entries.TryGetValue(entry.State.Id, out var old) && ReferenceEquals(old.View, entry.View))
                        changed.Add(old);
                    entry.View.Confirm(entry.State.Copy());
                }
            }
            catch (Exception cause)
            {
                var errors = new List<Exception> { cause };
                foreach (var old in changed)
                    try { old.View.Confirm(old.State.Copy()); } catch (Exception rollback) { errors.Add(rollback); }
                foreach (var view in created)
                    try { view.Dispose(); } catch (Exception cleanup) { errors.Add(cleanup); }
                if (errors.Count > 1) throw new AggregateException("projectile-restore-rollback-failed", errors);
                throw;
            }
            var previous = _entries;
            _entries = next;
            var disposalErrors = new List<Exception>();
            foreach (var old in previous)
                if (!next.TryGetValue(old.Key, out var kept) || !ReferenceEquals(kept.View, old.Value.View))
                    try { old.Value.View.Dispose(); } catch (Exception cleanup) { disposalErrors.Add(cleanup); }
            if (disposalErrors.Count > 0) throw new AggregateException("projectile-retirement-failed", disposalErrors);
        }

        public void Clear()
        {
            if (_entries.Count > 0) Restore([]);
        }
    }
}
