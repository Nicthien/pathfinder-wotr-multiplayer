using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using HarmonyLib;
using Kingmaker.EntitySystem.Persistence;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    // Keep the native file name, menu flow and write lifecycle. The optional
    // manifest only makes the host's native combat snapshot restorable later.
    [HarmonyPatch]
    internal static class NormalCombatSavePatches
    {
        private static readonly ConcurrentDictionary<ISaver, CombatSaveManifest> Pending = new();

        [HarmonyPatch(typeof(ZipSaver), nameof(ZipSaver.SaveJson))]
        [HarmonyPrefix]
        private static void SaveJsonPrefix(ZipSaver __instance, string name, ref string json)
        {
            if (!string.Equals(name, "tbm", StringComparison.Ordinal) || json == null) return;
            try
            {
                var manifest = (Main.Multiplayer as Services.Multiplayer)?.CaptureNormalCombatSave(ref json);
                if (manifest == null) { Pending.TryRemove(__instance, out _); return; }
                Pending[__instance] = manifest;
            }
            catch (Exception error)
            {
                Pending.TryRemove(__instance, out _);
                Main.GetLogger<Services.MultiplayerHost>().LogWarning(error,
                    "[CombatSave] Native save will not contain combat-resume metadata.");
            }
        }

        [HarmonyPatch(typeof(ZipSaver), nameof(ZipSaver.Save))]
        [HarmonyPrefix]
        private static void SavePrefix(ZipSaver __instance)
        {
            if (!Pending.TryGetValue(__instance, out var manifest)) return;
            try
            {
                // Ionic cannot Extract entries which have only been staged in
                // memory. Flush ZipFile itself (without disposing ZipSaver), then
                // hash the exact native bytes before the outer Save commits the manifest.
                var zipField = AccessTools.Field(typeof(ZipSaver), "m_ZipFile")
                    ?? throw new InvalidOperationException("native-save-zip-layout-unsupported");
                var zip = zipField.GetValue(__instance)
                    ?? throw new InvalidOperationException("native-save-zip-not-ready");
                var flush = AccessTools.Method(zip.GetType(), "Save", Type.EmptyTypes)
                    ?? throw new InvalidOperationException("native-save-zip-flush-unsupported");
                flush.Invoke(zip, null);
                CombatSaveArchive.AddToNativeSave(__instance, manifest);
            }
            catch (Exception error)
            {
                // Validation happens before the manifest write. Preserve the
                // native save rather than blocking it or publishing bad metadata.
                Main.GetLogger<Services.MultiplayerHost>().LogWarning(error,
                    "[CombatSave] Native save metadata validation failed; ordinary native save preserved.");
            }
        }

        [HarmonyPatch(typeof(ZipSaver), nameof(ZipSaver.Dispose))]
        [HarmonyPostfix]
        private static void DisposePostfix(ZipSaver __instance) => Pending.TryRemove(__instance, out _);

        [HarmonyPatch(typeof(ZipSaver), nameof(ZipSaver.Clear))]
        [HarmonyPostfix]
        private static void ClearPostfix(ZipSaver __instance) => Pending.TryRemove(__instance, out _);
    }
}
