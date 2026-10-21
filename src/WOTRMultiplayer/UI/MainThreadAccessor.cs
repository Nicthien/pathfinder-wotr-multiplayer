using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Abstractions.Unity;

namespace WOTRMultiplayer.UI
{
    public class MainThreadAccessor : IMainThreadAccessor
    {
        private readonly ConcurrentQueue<Action> _pending = new();
        public void Post(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            // Queue without ever searching/creating a Unity object on a network thread.
            _pending.Enqueue(action);
        }

        internal void Drain()
        {
            // UMM invokes this exclusively on the Unity thread.
            for (var i = 0; i < 256 && _pending.TryDequeue(out var action); i++)
            {
                try { action(); }
                catch (Exception failure) { Main.GetLogger<MainThreadAccessor>().LogError(failure, "[MainThread] Queued game action failed"); }
            }
        }
    }
}
