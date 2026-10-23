using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.Utility;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Suspends only the known native area subscriber graph. No HandleEnd,
    // OnDispose, UnitExit, fact disposal or AreaEffectDestroyed notification.
    public sealed class RealTimeNativeAreaRetirement : IDisposable
    {
        private static FieldInfo Field(Type type, string name) => AccessTools.Field(type, name)
            ?? throw new MissingFieldException(type.FullName, name);
        private readonly AreaEffectEntityData _area;
        private readonly object _world;
        private readonly object _marker = new();
        private readonly Action<object> _subscribe;
        private readonly Func<object, bool> _isSubscribed;
        private readonly List<object> _removed = new();
        private readonly List<Action> _restoreOrder = new();
        private readonly HashSet<IList> _capturedLists = new();
        private readonly List<object> _adapters;
        private readonly List<object> _originallySubscribed;
        private readonly bool _wasGloballySubscribed;
        private bool _committed;
        private bool _disposed;
        private bool _rollbackUnsafe;

        public static bool CanRetire(AreaEffectEntityData area) => area != null
            && area.GetType() == typeof(AreaEffectEntityData)
            && !RealTimeAreaReplicaFactory.IsReplica(area) && !area.IsDisposed
            && area.Facts != null && area.Facts.List.Count == 0 && area.Parts != null
            && ((ICollection)Field(typeof(EntityPartsManager), "m_Parts").GetValue(area.Parts)).Count == 0;

        private static IEnumerable<IList> SubscriberLists(object manager, IEnumerable<Type> interfaces)
        {
            if (manager == null) yield break;
            var listeners = Field(manager.GetType(), "m_Listeners").GetValue(manager);
            var dictionary = listeners as IDictionary
                ?? (IDictionary)Field(listeners.GetType(), "m_Dictionary").GetValue(listeners);
            foreach (var key in interfaces)
            {
                var list = dictionary.Contains(key) ? dictionary[key] : null;
                if (list == null) { yield return null; continue; }
                if ((bool)(CountableFlag)Field(list.GetType(), "Executing").GetValue(list))
                    throw new InvalidOperationException("native-area-subscription-dispatch-in-progress");
                yield return (IList)Field(list.GetType(), "List").GetValue(list);
            }
        }

        private static bool ContainsInAll(object manager, IEnumerable<Type> interfaces, object subscriber)
        {
            var counts = SubscriberLists(manager, interfaces).Select(list => list == null ? 0
                : list.Cast<object>().Count(value => ReferenceEquals(value, subscriber))).ToList();
            if (counts.All(count => count == 0)) return false;
            if (counts.Count != 0 && counts.All(count => count == 1)) return true;
            throw new InvalidOperationException("native-area-partial-or-duplicate-subscription");
        }

        private void CaptureOrder(object subscriber, List<object> subscribers)
        {
            Func<object> manager; IEnumerable<Type> interfaces;
            if (subscriber is AreaEffectEntityData area)
            {
                manager = () => EventBus.GlobalSubscribers;
                interfaces = InterfaceFinder<IGlobalSubscriber>.GetSubscriberInterfaces(area);
            }
            else
            {
                var adapter = (IInitiatorRulebookSubscriber)subscriber;
                var unit = adapter.GetSubscribingUnit();
                manager = () => RulebookEventBus.InitiatorRulebookSubscribers.Get(unit);
                interfaces = InterfaceFinder<IInitiatorRulebookSubscriber>.GetSubscribedRulebookTypes(adapter);
            }
            foreach (var key in interfaces)
            {
                var list = SubscriberLists(manager(), new[] { key }).SingleOrDefault();
                if (list == null) continue;
                if (!_capturedLists.Add(list)) continue;
                var original = list.Cast<object>().ToArray();
                bool Owned(object value) => subscribers.Any(owner => ReferenceEquals(owner, value));
                _restoreOrder.Add(() =>
                {
                    // Native Subscribe appends. Restore the exact callback order,
                    // but never overwrite unrelated subscriptions changed meanwhile.
                    var live = SubscriberLists(manager(), new[] { key }).SingleOrDefault()
                        ?? throw new InvalidOperationException("native-area-subscription-list-rollback-missing");
                    var current = live.Cast<object>().ToArray();
                    if (!current.Where(value => !Owned(value)).SequenceEqual(original.Where(value => !Owned(value)))
                        || !current.Where(Owned).OrderBy(value => subscribers.IndexOf(value))
                            .SequenceEqual(original.Where(Owned).OrderBy(value => subscribers.IndexOf(value))))
                        throw new InvalidOperationException("native-area-subscription-order-rollback-conflict");
                    live.Clear(); foreach (var value in original) live.Add(value);
                });
            }
        }

        private static bool IsNativelySubscribed(object subscriber)
        {
            if (subscriber is AreaEffectEntityData area)
                return ContainsInAll(EventBus.GlobalSubscribers,
                    InterfaceFinder<IGlobalSubscriber>.GetSubscriberInterfaces(area), area);
            if (subscriber is IInitiatorRulebookSubscriber adapter)
            {
                var unit = adapter.GetSubscribingUnit();
                return unit != null && ContainsInAll(RulebookEventBus.InitiatorRulebookSubscribers.Get(unit),
                    InterfaceFinder<IInitiatorRulebookSubscriber>.GetSubscribedRulebookTypes(adapter), adapter);
            }
            throw new InvalidOperationException("native-area-unknown-subscriber");
        }

        public RealTimeNativeAreaRetirement(AreaEffectEntityData area, object world,
            Action<object> unsubscribe = null, Action<object> subscribe = null, Func<object, bool> isSubscribed = null)
        {
            if (!CanRetire(area) || world == null) throw new InvalidOperationException("unsupported-native-area-retirement");
            _area = area; _world = world;
            _subscribe = subscribe ?? (subscriber => { EventBus.Subscribe(subscriber); });
            _isSubscribed = isSubscribed ?? IsNativelySubscribed;
            unsubscribe ??= EventBus.Unsubscribe;
            _wasGloballySubscribed = area.IsSubscribedOnEventBus;
            var adapters = ((IEnumerable)Field(typeof(AreaEffectEntityData), "m_DamageTriggersCopies").GetValue(area)
                ?? Array.Empty<object>()).Cast<object>().ToList();
            _adapters = adapters;
            var adapterType = typeof(AreaEffectEntityData).GetNestedType("DamageTriggerAdapter", BindingFlags.NonPublic | BindingFlags.Public);
            if (adapters.Any(adapter => adapter == null || adapter.GetType() != adapterType
                || !ReferenceEquals(Field(adapterType, "m_Area").GetValue(adapter), area))
                || adapters.Distinct().Count() != adapters.Count)
                throw new InvalidOperationException("native-area-damage-adapter-ownership-invalid");
            if (_isSubscribed(area) != _wasGloballySubscribed)
                throw new InvalidOperationException("native-area-subscription-flag-mismatch");
            var subscribers = new[] { (object)area }.Concat(adapters).Where(_isSubscribed).ToList();
            _originallySubscribed = subscribers;
            if (isSubscribed == null) foreach (var subscriber in subscribers) CaptureOrder(subscriber, subscribers);
            RealTimeAreaReplicaFactory.MarkNativeInert(area, _marker);
            RealTimeNativeGraphRepair.Add(world, _marker);
            try
            {
                foreach (var subscriber in subscribers)
                {
                    // Record first: an unsubscribe may mutate then throw.
                    _removed.Add(subscriber);
                    unsubscribe(subscriber);
                }
                Field(typeof(EntityDataBase), "<IsSubscribedOnEventBus>k__BackingField").SetValue(area, false);
                ValidateForCommit();
            }
            catch (Exception failure)
            {
                try { Dispose(); }
                catch (Exception rollback) { throw new AggregateException("native-area-retirement-and-rollback-failed", failure, rollback); }
                throw;
            }
        }

        public void ValidateForCommit()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RealTimeNativeAreaRetirement));
            var adapters = ((IEnumerable)Field(typeof(AreaEffectEntityData), "m_DamageTriggersCopies").GetValue(_area)
                ?? Array.Empty<object>()).Cast<object>();
            if (!RealTimeAreaReplicaFactory.IsNativeInertOwner(_area, _marker)
                || !adapters.SequenceEqual(_adapters) || _area.IsSubscribedOnEventBus
                || new[] { (object)_area }.Concat(_adapters).Any(_isSubscribed))
                throw new InvalidOperationException("native-area-retirement-ownership-changed");
        }
        public void Commit() { ValidateForCommit(); _committed = true; }
        internal void ResolveAfterVerifiedNativeGraphRebuild()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RealTimeNativeAreaRetirement));
            if (!_committed) throw new InvalidOperationException("native-area-retirement-not-committed");
            // A caller may resolve this external obligation only after the
            // complete replacement graph has passed its authoritative topology
            // and temporal verification. Until then the retired area's native
            // subscribers remain deliberately inert and saving stays blocked.
            ValidateForCommit();
            RealTimeAreaReplicaFactory.ReleaseNativeInert(_area, _marker);
            RealTimeNativeGraphRepair.Remove(_world, _marker);
        }
        internal void KeepSuspendedAfterRollbackFailure() => _rollbackUnsafe = true;
        public void Dispose()
        {
            if (_disposed) return;
            // Registry/pool rollback failed: subscriptions must not be enabled
            // against a partially restored world. Keep the external obligation.
            if (_rollbackUnsafe) { _disposed = true; return; }
            if (!_committed)
            {
                List<Exception> failures = null;
                foreach (var subscriber in _removed)
                {
                    try { if (!_isSubscribed(subscriber)) _subscribe(subscriber); }
                    catch (Exception failure) { (failures ??= new()).Add(failure); }
                }
                if (failures != null) throw new AggregateException("native-area-subscription-rollback-failed", failures);
                if (_removed.Any(subscriber => !_isSubscribed(subscriber)))
                    throw new InvalidOperationException("native-area-subscription-rollback-unverified");
                var adapters = ((IEnumerable)Field(typeof(AreaEffectEntityData), "m_DamageTriggersCopies").GetValue(_area)
                    ?? Array.Empty<object>()).Cast<object>();
                if (!adapters.SequenceEqual(_adapters)
                    || new[] { (object)_area }.Concat(_adapters)
                        .Any(subscriber => _isSubscribed(subscriber) != _originallySubscribed.Contains(subscriber)))
                    throw new InvalidOperationException("native-area-subscription-rollback-graph-changed");
                foreach (var restore in _restoreOrder) restore();
                Field(typeof(EntityDataBase), "<IsSubscribedOnEventBus>k__BackingField").SetValue(_area, _wasGloballySubscribed);
                RealTimeAreaReplicaFactory.ReleaseNativeInert(_area, _marker);
                RealTimeNativeGraphRepair.Remove(_world, _marker);
            }
            // A committed obligation survives removal from every entity pool.
            // Only a verified native graph rebuild may eventually resolve it.
            _disposed = true;
        }
    }
}
