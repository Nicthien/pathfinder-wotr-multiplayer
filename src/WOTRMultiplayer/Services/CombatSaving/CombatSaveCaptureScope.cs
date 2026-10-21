using System;
using Kingmaker;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Items;
using Kingmaker.Blueprints.Items;

namespace WOTRMultiplayer.Services.CombatSaving
{
    // Synchronous native save preparation only; never spans a frame or a load.
    internal sealed class CombatSaveCaptureScope : IDisposable
    {
        [ThreadStatic] private static CombatSaveCaptureScope current;
        private readonly Game game;
        private readonly object world;
        private readonly Dictionary<object, Queue<ItemEntityWeapon>> weapons = new();
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

        private sealed class WeaponRestoration : IDisposable
        {
            private readonly CombatSaveCaptureScope scope;
            private readonly Queue<ItemEntityWeapon> previous;
            internal WeaponRestoration(CombatSaveCaptureScope scope, Queue<ItemEntityWeapon> queue)
            { this.scope = scope; previous = scope.restoring; scope.restoring = queue; }
            public void Dispose() { scope.restoring = previous; }
        }

        internal CombatSaveCaptureScope(Game game)
        {
            if (current != null || !CombatSaveActivation.Enabled || !CombatSavePauseLease.HoldsWorld(game))
                throw new InvalidOperationException("combat-save-capture-scope-not-authorized");
            this.game = game;
            world = game.State;
            current = this;
        }

        internal static bool Active => current != null && ReferenceEquals(Game.Instance, current.game)
            && ReferenceEquals(current.game.State, current.world) && CombatSavePauseLease.HoldsWorld(current.game);

        public void Dispose()
        {
            if (ReferenceEquals(current, this)) current = null;
        }
    }
}
