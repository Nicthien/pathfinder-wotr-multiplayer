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
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Equipment;
using WOTRMultiplayer.Entities.Spells;
using WOTRMultiplayer.Entities.Units;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Presentation facts are intentionally not mechanical reconstructions.
    // They must never be used as evidence that TurnBased can resume safely.
    public static class RealTimeBuffReplicaStore
    {
        private static readonly ConditionalWeakTable<Buff, ReplicaMetadata> Replicas = new();
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
            if (ReferenceEquals(blueprint, null) || state == null
                || context == null && (state.ContextChain == null || state.ContextChain.Count != 0)
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
            Replicas.Add(buff, new ReplicaMetadata(Clone(state)));
            return buff;
        }

        // A detached presentation context contains locally resolved game objects. Some
        // of those objects (notably AbilityData) legitimately have a different runtime
        // identity on the client. Re-mapping such a context would therefore manufacture
        // a different canonical snapshot even though the replica represents the host's
        // exact buff. Keep the last host-confirmed wire projection beside the inert fact
        // and use it when the client captures its next acknowledgement.
        public static void Confirm(Buff buff, NetworkBuff state)
        {
            if (buff == null || state == null || !Replicas.TryGetValue(buff, out var metadata)
                || !string.Equals(buff.UniqueId, state.Id, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(buff.Blueprint.AssetGuid.ToString(), state.BlueprintId,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("invalid-buff-replica-confirmation");
            metadata.Confirmed = Clone(state);
        }

        public static NetworkBuff ProjectConfirmed(Buff buff, NetworkBuff locallyMapped)
        {
            if (buff == null || locallyMapped == null || !Replicas.TryGetValue(buff, out var metadata))
                return locallyMapped;
            var result = Clone(metadata.Confirmed);
            // Durations are relative to each instance's game clock. Preserve the values
            // freshly mapped from the local inert fact while retaining every canonical
            // identity and context value supplied by the authoritative host.
            result.TimeLeft = locallyMapped.TimeLeft;
            result.NextTickTime = locallyMapped.NextTickTime;
            result.NextResourceSpendingTime = locallyMapped.NextResourceSpendingTime;
            return result;
        }

        private sealed class ReplicaMetadata
        {
            public ReplicaMetadata(NetworkBuff confirmed) => Confirmed = confirmed;
            public NetworkBuff Confirmed { get; set; }
        }

        private static NetworkBuff Clone(NetworkBuff value) => value == null ? null : new NetworkBuff
        {
            ContextChain = value.ContextChain?.Select(Clone).ToList(),
            Id = value.Id,
            BlueprintId = value.BlueprintId,
            Name = value.Name,
            IsPermanent = value.IsPermanent,
            IsHidden = value.IsHidden,
            TimeLeft = value.TimeLeft,
            NextTickTime = value.NextTickTime,
            NextResourceSpendingTime = value.NextResourceSpendingTime,
            CasterId = value.CasterId,
            Rank = value.Rank,
            SourceAbilityCasterId = value.SourceAbilityCasterId,
            SourceAbility = CloneAbility(value.SourceAbility),
            SourceAbilityParams = Clone(value.SourceAbilityParams)
        };

        private static NetworkBuffContextState Clone(NetworkBuffContextState value) => value == null ? null
            : new NetworkBuffContextState
            {
                BlueprintId = value.BlueprintId,
                CasterId = value.CasterId,
                OwnerId = value.OwnerId,
                IsAbilityContext = value.IsAbilityContext,
                SourceAbility = CloneAbility(value.SourceAbility),
                Params = Clone(value.Params),
                Ranks = value.Ranks?.ToList(),
                SharedValues = value.SharedValues?.ToList(),
                Properties = value.Properties?.ToList(),
                SpellDescriptor = value.SpellDescriptor,
                SpellSchool = value.SpellSchool,
                SpellLevel = value.SpellLevel,
                DisableFx = value.DisableFx,
                Direction = Clone(value.Direction),
                SourceItemId = value.SourceItemId,
                MainTarget = Clone(value.MainTarget)
            };

        internal static NetworkAbility CloneAbility(NetworkAbility value) => value == null ? null : new NetworkAbility
        {
            Id = value.Id,
            BlueprintId = value.BlueprintId,
            SpellbookId = value.SpellbookId,
            Name = value.Name,
            ConvertedFromId = value.ConvertedFromId,
            SpellLevel = value.SpellLevel,
            Metamagic = value.Metamagic,
            ParamSpellLevel = value.ParamSpellLevel,
            ParamSpellBookId = value.ParamSpellBookId,
            ParamSpellSlot = Clone(value.ParamSpellSlot),
            SourceItem = Clone(value.SourceItem),
            PotionForOther = value.PotionForOther
        };

        private static NetworkAbilityParams Clone(NetworkAbilityParams value) => value == null ? null
            : new NetworkAbilityParams
            {
                SpellLevel = value.SpellLevel,
                CasterLevel = value.CasterLevel,
                Concentration = value.Concentration,
                DC = value.DC,
                SpellSource = value.SpellSource,
                Metamagic = value.Metamagic,
                RankBonus = value.RankBonus
            };

        private static NetworkAbilityParamSpellSlot Clone(NetworkAbilityParamSpellSlot value) => value == null ? null
            : new NetworkAbilityParamSpellSlot
            {
                SpellbookId = value.SpellbookId,
                SpellLevel = value.SpellLevel,
                Slot = value.Slot == null ? null : new NetworkSpellSlot
                {
                    Index = value.Slot.Index,
                    Type = value.Slot.Type
                }
            };

        private static NetworkItem Clone(NetworkItem value) => value == null ? null : new NetworkItem
        {
            Id = value.Id,
            BlueprintId = value.BlueprintId,
            Name = value.Name,
            Count = value.Count,
            Cost = value.Cost,
            EnchantmentValue = value.EnchantmentValue,
            Enchantments = value.Enchantments?.ToList(),
            HoldingSlotOwnerId = value.HoldingSlotOwnerId,
            CollectionOwnerRef = value.CollectionOwnerRef,
            MetamagicMask = value.MetamagicMask
        };

        private static NetworkVector3 Clone(NetworkVector3 value) => value == null ? null
            : new NetworkVector3(value.X, value.Y, value.Z);

        private static NetworkTargetWrapper Clone(NetworkTargetWrapper value) => value == null ? null
            : new NetworkTargetWrapper(Clone(value.Point), value.Orientation, value.UnitId);

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
