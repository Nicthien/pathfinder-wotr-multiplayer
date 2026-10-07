using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence.JsonUtility;
using Kingmaker.UnitLogic.Buffs;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using Kingmaker.UnitLogic.Mechanics;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Presentation facts are intentionally not mechanical reconstructions.
    // They must never be used as evidence that TurnBased can resume safely.
    public static class RealTimeBuffReplicaStore
    {
        private static readonly ConditionalWeakTable<Buff, object> Replicas = new();
        private static readonly FieldInfo ContextField = AccessTools.Field(typeof(Buff), "m_Context")
            ?? throw new MissingFieldException(typeof(Buff).FullName, "m_Context");

        public static bool IsReplica(Buff buff) => buff != null && Replicas.TryGetValue(buff, out _);

        public static bool RequiresMechanicalRebuild(EntityFactsManager manager) => manager != null
            && manager.GetAll<Buff>().Any(IsReplica);

        public static bool RequiresMechanicalRebuild(IEnumerable<UnitEntityData> units) => units != null
            && units.Any(unit => unit != null && RequiresMechanicalRebuild(unit.Facts));

        public static void RequireMechanicalState(IEnumerable<UnitEntityData> units)
        {
            if (RequiresMechanicalRebuild(units))
                throw new InvalidOperationException("presentation-buffs-require-mechanical-reconstruction");
        }

        public static Buff CreateDetached(BlueprintBuff blueprint, MechanicsContext context, NetworkBuff state, TimeSpan baseTime)
        {
            if (ReferenceEquals(blueprint, null) || context == null || state == null
                || string.IsNullOrWhiteSpace(state.Id) || string.IsNullOrWhiteSpace(state.BlueprintId)
                || !string.Equals(blueprint.AssetGuid.ToString(), state.BlueprintId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("invalid-buff-replica-source");
            // This constructor only initializes the fact's empty Components
            // list. The ordinary blueprint constructor invokes Setup and
            // CreateRuntimeFactComponent, and is deliberately not used.
            var buff = new Buff(default(JsonConstructorMark));
            var fact = (EntityFact)buff;
            fact.Blueprint = blueprint;
            fact.UniqueId = state.Id;
            fact.AttachTime = baseTime;
            fact.SuppressActivationOnAttach = true;
            fact.IsPostLoadExecuted = true;
            ContextField.SetValue(buff, context);
            RealTimeBuffReplication.ApplyValues(buff, state, baseTime);
            if (fact.Components.Count != 0 || fact.IsActive || fact.IsTurnedOn || fact.IsSubscribedOnEventBus)
                throw new InvalidOperationException("buff-replica-created-mechanical-components");
            Replicas.Add(buff, new object());
            return buff;
        }

        public static Registration Register(EntityFactsManager manager, Buff buff)
        {
            if (manager == null || manager.Owner == null || !IsReplica(buff) || buff.Manager != null
                || manager.List.Any(fact => string.Equals(fact.UniqueId, buff.UniqueId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("invalid-or-duplicate-buff-replica-registration");
            // Mirror only the native list/cache bookkeeping. No Manager.Add,
            // Attach, event delegates, activation, subscription or PostLoad.
            manager.m_Facts.Add(buff);
            try
            {
                manager.m_Cache.Add(buff);
                ((EntityFact)buff).Manager = manager;
                return new Registration(manager, buff);
            }
            catch
            {
                manager.m_Facts.Remove(buff);
                manager.m_Cache.Remove(buff);
                ((EntityFact)buff).Manager = null;
                throw;
            }
        }

        public sealed class Registration : IDisposable
        {
            private EntityFactsManager _manager;
            private readonly Buff _buff;
            private bool _committed;
            internal Registration(EntityFactsManager manager, Buff buff) { _manager = manager; _buff = buff; }
            public void Commit()
            {
                if (_manager == null) throw new ObjectDisposedException(nameof(Registration));
                _committed = true;
            }
            public void Dispose()
            {
                var manager = _manager;
                if (manager == null) return;
                _manager = null;
                if (_committed) return;
                manager.m_Facts.Remove(_buff);
                manager.m_Cache.Remove(_buff);
                ((EntityFact)_buff).Manager = null;
            }
        }

        public static Removal RemoveReplica(EntityFactsManager manager, Buff buff)
        {
            if (manager == null || !IsReplica(buff) || !ReferenceEquals(buff.Manager, manager))
                throw new InvalidOperationException("invalid-buff-replica-removal");
            var index = manager.m_Facts.IndexOf(buff);
            if (index < 0) throw new InvalidOperationException("unregistered-buff-replica-removal");
            manager.m_Facts.RemoveAt(index);
            manager.m_Cache.Remove(buff);
            ((EntityFact)buff).Manager = null;
            return new Removal(manager, buff, index);
        }

        public sealed class Removal : IDisposable
        {
            private EntityFactsManager _manager;
            private readonly Buff _buff;
            private readonly int _index;
            private bool _committed;
            internal Removal(EntityFactsManager manager, Buff buff, int index)
            { _manager = manager; _buff = buff; _index = index; }
            public void Commit()
            {
                if (_manager == null) throw new ObjectDisposedException(nameof(Removal));
                _committed = true;
            }
            public void Dispose()
            {
                var manager = _manager;
                if (manager == null) return;
                _manager = null;
                if (_committed) return;
                if (_buff.Manager != null || manager.List.Any(fact => string.Equals(fact.UniqueId, _buff.UniqueId, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("buff-replica-removal-rollback-conflict");
                manager.m_Facts.Insert(Math.Min(_index, manager.m_Facts.Count), _buff);
                manager.m_Cache.Add(_buff);
                ((EntityFact)_buff).Manager = manager;
            }
        }
    }
}
