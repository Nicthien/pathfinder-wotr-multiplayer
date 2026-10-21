using System;
using Kingmaker;

namespace WOTRMultiplayer.Services.CombatSaving
{
    // Unity-thread only. This token owns neither another operation's pause nor a new world.
    internal sealed class CombatSavePauseLease : IDisposable
    {
        private static CombatSavePauseLease _current;
        private readonly Game _game;
        private readonly object _world;
        private readonly Func<bool> _safeToResume;
        private bool _keepPaused;
        internal static bool Active => _current != null;

        // Unlike HoldsWorld, this deliberately excludes a world protected by
        // the load/repair guard. It is used for behavior that is safe only
        // while a live save operation owns the unchanged world.
        internal static bool HoldsSaveWorld(Game game) => game != null && _current != null
            && ReferenceEquals(_current._game, game) && ReferenceEquals(_current._world, game.State);

        // Game.DoStartMode intentionally rejects Pause in TB combat. Hold its
        // simulation clock and turn controller instead, without reactivating
        // controllers or changing the turn's action state.
        internal static bool HoldsWorld(Game game) => game != null
            && (CombatSaveLoadState.HoldsWorld(game.State)
                || _current != null && ReferenceEquals(_current._game, game)
                    && ReferenceEquals(_current._world, game.State));
        internal static void FreezeClock(Game game)
        {
            if (!HoldsWorld(game)) return;
            game.TimeController.Suspend();
            game.TimeController.SetGameDeltaTime(0f);
        }
        internal static bool IsWorldPaused(Game game) => game != null && (game.IsPaused
            || HoldsWorld(game) && game.TimeController.DeltaTime == 0f && game.TimeController.GameDeltaTime == 0f);

        internal CombatSavePauseLease(Func<bool> safeToResume)
        {
            if (_current != null) throw new InvalidOperationException("combat-save-pause-already-owned");
            _game = Game.Instance ?? throw new InvalidOperationException("combat-save-no-game");
            _world = _game.State; _safeToResume = safeToResume;
            _current = this;
            FreezeClock(_game);
        }
        internal void Protect() => _keepPaused = true;
        public void Dispose()
        {
            if (!ReferenceEquals(_current, this)) return;
            if (ReferenceEquals(Game.Instance, _game) && ReferenceEquals(_game.State, _world)
                && (_keepPaused || !_safeToResume()))
            {
                // Unknown outcomes remain held even after this operation ends.
                if (CombatSaveLoadState.Owner == null) CombatSaveLoadState.Owner = this;
                CombatSaveLoadState.BindLoadedWorld(_world);
            }
            _current = null;
        }
    }
}
