using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Kingmaker;
using Kingmaker.EntitySystem.Persistence;
using Kingmaker.Utility;

namespace WOTRMultiplayer.Services
{
    public static class RealTimeTestProfileBootstrap
    {
        public const string EnvironmentVariable = "WOTR_RTWP_TEST_PROFILE";
        public const string MarkerFile = ".rtwp-test-profile";

        public static string ApplyIfRequested()
        {
#if !RTWP_HOST_PRESENTATION_PROTOTYPE
            return null;
#else
            var requested = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(requested)) return null;
            var profile = Path.GetFullPath(requested).TrimEnd(Path.DirectorySeparatorChar);
            var marker = Path.Combine(profile, MarkerFile);
            if (!File.Exists(marker))
                throw new InvalidOperationException($"RTwP test profile marker is missing: {marker}");
            Directory.CreateDirectory(profile);
            var cache = Path.Combine(profile, "Cache");
            var saves = Path.Combine(profile, SaveManager.SaveFolderName);
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(saves);

            SetApplicationPath(nameof(ApplicationPaths.persistentDataPath), profile);
            SetApplicationPath(nameof(ApplicationPaths.temporaryCachePath), cache);

            // The game resolves its log directory once and caches it in
            // BuildModeUtility; two isolated instances would otherwise share
            // GameLogFull.txt and fight over the file handle (Sharing violation
            // observed in the A0 startup check). Redirect every read to the
            // profile's own Logs folder, before any caller resolves the path.
            var logs = Path.Combine(profile, "Logs");
            Directory.CreateDirectory(logs);
            var harmony = new HarmonyLib.Harmony("wotrmultiplayer.rtwp.test-profile");
            var getter = typeof(BuildModeUtility).GetMethod("GetLogsDir",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(BuildModeUtility).FullName, "GetLogsDir");
            harmony.Patch(getter,
                postfix: new HarmonyLib.HarmonyMethod(
                    typeof(RealTimeTestProfileBootstrap).GetMethod(nameof(RedirectLogsDir),
                        BindingFlags.Static | BindingFlags.NonPublic)));

            var gameField = typeof(Game).GetField("s_Instance",
                BindingFlags.Static | BindingFlags.NonPublic);
            var game = gameField?.GetValue(null) as Game;
            if (game != null)
            {
                var manager = game.SaveManager;
                var updateTask = typeof(SaveManager).GetField("m_UpdateTask",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager);
                var savedGames = typeof(SaveManager).GetField("m_SavedGames",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager) as ICollection;
                var commit = typeof(SaveManager).GetField("m_CommitInProgress",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager) as bool?;
                if (updateTask != null || savedGames?.Count > 0 || commit == true)
                    throw new InvalidOperationException(
                        "The save manager already started; RTwP test profile isolation is unsafe.");
                typeof(SaveManager).GetField("m_SavePath", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(manager, saves);
            }
            return profile;
#endif
        }

#if RTWP_HOST_PRESENTATION_PROTOTYPE
        private static void RedirectLogsDir(ref string __result)
        {
            var profile = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(profile))
                __result = Path.Combine(profile, "Logs");
        }
#endif

        private static void SetApplicationPath(string propertyName, string value)
        {
            var property = typeof(ApplicationPaths).GetProperty(propertyName,
                BindingFlags.Static | BindingFlags.Public);
            var setter = property?.GetSetMethod(true)
                ?? throw new MissingMethodException(typeof(ApplicationPaths).FullName, propertyName);
            setter.Invoke(null, new object[] { value });
        }
    }
}
