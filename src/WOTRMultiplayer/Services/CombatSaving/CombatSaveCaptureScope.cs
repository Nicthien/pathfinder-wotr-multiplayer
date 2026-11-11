using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Items;
using Kingmaker.Blueprints.Items;
using Kingmaker.UnitLogic.Buffs;

namespace WOTRMultiplayer.Services.CombatSaving
{
    // Synchronous native save preparation only; never spans a frame or a load.
    internal sealed class CombatSaveCaptureScope : IDisposable
    {
        [ThreadStatic] private static CombatSaveCaptureScope current;
        private readonly Game game;
        private readonly object world;
        private readonly Func<bool> nativeBarrierOwned;
        private readonly Dictionary<object, Queue<ItemEntityWeapon>> weapons = new();
        private readonly Dictionary<EntityFact, CapturedFact> capturedFacts = new();
        private readonly Dictionary<string, CapturedFact> capturedStableFacts = new(StringComparer.Ordinal);
        private readonly HashSet<string> ambiguousStableFacts = new(StringComparer.Ordinal);
        private Queue<ItemEntityWeapon> restoring;

        internal static void RememberWeapons(object owner, IEnumerable<ItemEntityWeapon> values)
        {
            if (!Active) return;
            current.weapons.Add(owner, new Queue<ItemEntityWeapon>(values.Where(value => value != null)));
        }

        internal static IDisposable RestoreWeapons(object owner)
        {
            if (!Active || !current.weapons.TryGetValue(owner, out var queue)) return null;
            return new WeaponRestoration(current, queue);
        }

        internal static ItemEntityWeapon ReuseWeapon(BlueprintItem blueprint)
        {
            if (!Active || current.restoring == null || current.restoring.Count == 0) return null;
            var weapon = current.restoring.Peek();
            if (weapon.Blueprint != blueprint) throw new InvalidOperationException("combat-save-natural-weapon-order-changed");
            return current.restoring.Dequeue();
        }

        internal void VerifyWeaponsRestored()
        {
            if (restoring != null || weapons.Values.Any(queue => queue.Count != 0))
                throw new InvalidOperationException("combat-save-natural-weapon-not-restored");
        }

        private void CaptureEntityFacts(EntityDataBase entity)
        {
            if (entity?.Facts?.List == null) return;
            foreach (var fact in entity.Facts.List)
            {
                if (fact == null || string.IsNullOrWhiteSpace(fact.UniqueId)) continue;
                var captured = CapturedFact.From(fact);
                capturedFacts[fact] = captured;
                var stableKey = captured.StableKey;
                if (string.IsNullOrWhiteSpace(stableKey)) continue;
                if (capturedStableFacts.TryGetValue(stableKey, out var existing) && existing.UniqueId != captured.UniqueId)
                    ambiguousStableFacts.Add(stableKey);
                capturedStableFacts[stableKey] = captured;
            }
        }

        internal static bool TryReuseCapturedFact(EntityFact fact, out string uniqueId)
        {
            uniqueId = null;
            if (!Active || fact == null) return false;
            if (current.capturedFacts.TryGetValue(fact, out var captured))
            {
                captured.ApplyTo(fact);
                uniqueId = captured.UniqueId;
                return true;
            }
            var candidate = CapturedFact.From(fact);
            var stableKey = candidate.StableKey;
            if (string.IsNullOrWhiteSpace(stableKey)
                || current.ambiguousStableFacts.Contains(stableKey)
                || !current.capturedStableFacts.TryGetValue(stableKey, out var remembered))
                return false;
            remembered.ApplyTo(fact);
            uniqueId = remembered.UniqueId;
            return true;
        }

        private sealed class CapturedFact
        {
            public string UniqueId { get; }
            public string StableKey { get; }
            public int Rank { get; }
            public TimeSpan EndTime { get; }
            public TimeSpan NextTickTime { get; }
            public TimeSpan NextResourceSpendingTime { get; }

            private CapturedFact(string uniqueId, string stableKey, int rank, TimeSpan endTime,
                TimeSpan nextTickTime, TimeSpan nextResourceSpendingTime)
            {
                UniqueId = uniqueId; StableKey = stableKey; Rank = rank; EndTime = endTime;
                NextTickTime = nextTickTime; NextResourceSpendingTime = nextResourceSpendingTime;
            }

            internal static CapturedFact From(EntityFact fact)
                => fact is Buff buff
                    ? new(fact.UniqueId, StableKey(fact), buff.Rank, buff.EndTime,
                          buff.NextTickTime, buff.NextResourceSpendingTime)
                    : new(fact.UniqueId, StableKey(fact), 0, TimeSpan.Zero,
                          TimeSpan.Zero, TimeSpan.Zero);

            internal void ApplyTo(EntityFact fact)
            {
                fact.UniqueId = UniqueId;
                if (fact is Buff buff)
                {
                    buff.Rank = Rank;
                    buff.EndTime = EndTime;
                    buff.NextTickTime = NextTickTime;
                    buff.NextResourceSpendingTime = NextResourceSpendingTime;
                }
            }
        }

        internal static string StableKey(EntityFact fact)
            => $"{fact.Owner?.UniqueId}|{fact.Blueprint?.AssetGuid.ToString()}|"
               + $"{(fact is Buff buff ? buff.Rank : 0)}";

        private sealed class WeaponRestoration : IDisposable
        {
            private readonly CombatSaveCaptureScope scope;
            private readonly Queue<ItemEntityWeapon> previous;
            internal WeaponRestoration(CombatSaveCaptureScope scope, Queue<ItemEntityWeapon> queue)
            { this.scope = scope; previous = scope.restoring; scope.restoring = queue; }
            public void Dispose() { scope.restoring = previous; }
        }

        internal CombatSaveCaptureScope(Game game, Func<bool> nativeBarrierOwned = null)
        {
            if (current != null || !CombatSaveActivation.Enabled
                || !CombatSavePauseLease.HoldsWorld(game) && nativeBarrierOwned?.Invoke() != true)
                throw new InvalidOperationException("combat-save-capture-scope-not-authorized");
            this.game = game;
            world = game.State;
            this.nativeBarrierOwned = nativeBarrierOwned;
            CaptureFacts(game);
            current = this;
        }

        internal static bool Active => current != null && ReferenceEquals(Game.Instance, current.game)
            && ReferenceEquals(current.game.State, current.world)
            && (CombatSavePauseLease.HoldsWorld(current.game) || current.nativeBarrierOwned?.Invoke() == true);

        private void CaptureFacts(Game game)
        {
            if (game?.State == null) return;
            CaptureEntityFacts(game.Player);
            if (game.State.Units != null)
                foreach (var unit in game.State.Units.All) CaptureEntityFacts(unit);
            if (game.State.AreaEffects != null)
                foreach (var area in game.State.AreaEffects) CaptureEntityFacts(area);
            if (game.State.LoadedAreaState != null)
                foreach (var entity in game.State.LoadedAreaState.AllEntityData) CaptureEntityFacts(entity);
            if (game.Player?.CrossSceneState != null)
                foreach (var entity in game.Player.CrossSceneState.AllEntityData) CaptureEntityFacts(entity);
        }

        public void Dispose()
        {
            if (ReferenceEquals(current, this))
            {
                current = null;
                capturedFacts.Clear();
                capturedStableFacts.Clear();
                ambiguousStableFacts.Clear();
            }
        }
    }
}
