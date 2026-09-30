using System;
using System.Threading;

namespace WOTRMultiplayer.Services
{
    public static class RealTimeCommandExecutionScope
    {
        private static readonly AsyncLocal<int> Depth = new();

        public static bool IsActive => Depth.Value > 0;

        public static IDisposable Enter()
        {
            Depth.Value++;
            return new Scope();
        }

        private sealed class Scope : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                Depth.Value--;
                _disposed = true;
            }
        }
    }
}
