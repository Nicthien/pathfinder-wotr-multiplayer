namespace WOTRMultiplayer.Services.CombatSaving
{
    // Main-thread scoped, set before the native loader can dispose the old world.
    internal static class CombatSaveLoadState
    {
        private static object _owner, _world;
        internal static object Owner
        {
            get => _owner;
            set { _owner = value; _world = null; }
        }
        internal static void BindLoadedWorld(object world)
        {
            if (_owner == null || world == null) throw new System.InvalidOperationException("combat-save-no-load-owner");
            _world = world;
        }
        // A failed world stays protected even after a network disconnect. A later
        // ordinary world must not inherit the failed world's combat lock.
        internal static bool Active => _owner != null
            && (_world == null || ReferenceEquals(Kingmaker.Game.Instance?.State, _world));

        internal static bool HoldsWorld(object world) => _owner != null && _world != null
            && ReferenceEquals(world, _world);
    }
}
