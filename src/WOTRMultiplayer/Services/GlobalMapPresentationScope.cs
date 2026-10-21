using System;
using WOTRMultiplayer.Services.Random;

namespace WOTRMultiplayer.Services
{
    internal static class GlobalMapPresentationScope
    {
        public static string Current => Main.Multiplayer.GetSeededContext(
            SeedKind.Session | SeedKind.LoadedSaveSeed | SeedKind.AreaSeed).Id;
        public static bool Matches(string scope) => !string.IsNullOrEmpty(scope)
            && string.Equals(scope, Current, StringComparison.Ordinal);
    }
}
