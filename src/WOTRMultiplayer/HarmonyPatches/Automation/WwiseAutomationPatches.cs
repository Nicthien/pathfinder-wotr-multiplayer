using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Kingmaker.Sound;
using Kingmaker.Visual.Sound;

namespace WOTRMultiplayer.HarmonyPatches.Automation
{
    /// <summary>
    /// Allows a second isolated automation instance to run on machines where the
    /// native Wwise initialization blocks. This path is opt-in and is never used
    /// by a normal player launch.
    /// </summary>
    [HarmonyPatch]
    internal static class WwiseAutomationPatches
    {
        private const string DisableAudioVariable = "WOTR_RTWP_AUTOMATION_DISABLE_AUDIO";

        [HarmonyPatch(typeof(AkWwiseInitializationSettings), nameof(AkWwiseInitializationSettings.InitializeSoundEngine))]
        [HarmonyPrefix]
        private static bool InitializeSoundEnginePrefix(ref bool __result)
        {
            if (!IsAudioDisabledForAutomation())
                return true;

            UnityEngine.Debug.LogWarning("[WOTRMultiplayerRTwP] Wwise disabled for this isolated automation instance.");
            __result = false;
            return false;
        }

        [HarmonyPatch(typeof(SoundState), nameof(SoundState.ResetState))]
        [HarmonyPrefix]
        private static bool ResetSoundStatePrefix()
        {
            return !IsAudioDisabledForAutomation();
        }

        private static bool IsInitializedForStartup()
        {
            return IsAudioDisabledForAutomation() || AkSoundEngine.IsInitialized();
        }

        private static bool IsAudioDisabledForAutomation()
        {
            return string.Equals(Environment.GetEnvironmentVariable("WOTR_RTWP_AUTOMATION"), "1", StringComparison.Ordinal)
                   && string.Equals(Environment.GetEnvironmentVariable(DisableAudioVariable), "1", StringComparison.Ordinal);
        }

        private static IEnumerable<MethodBase> AkMethodsReturning(Type returnType)
        {
            return typeof(AkSoundEngine).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => !method.IsGenericMethodDefinition && method.ReturnType == returnType);
        }

        [HarmonyPatch]
        private static class StartupAudioWaitPatches
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var ownerName in new[]
                         {
                             "Kingmaker.UI.MainMenuUI.SplashScreenController",
                             "Kingmaker.UI.MainMenuUI.MainMenuLogoAnimator"
                         })
                {
                    var owner = AccessTools.TypeByName(ownerName);
                    if (owner == null) continue;
                    foreach (var nested in owner.GetNestedTypes(BindingFlags.NonPublic)
                                 .Where(type => type.Name.Contains("<DelayedShow>")))
                    {
                        var moveNext = AccessTools.Method(nested, "MoveNext");
                        if (moveNext != null) yield return moveNext;
                    }
                }
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var original = AccessTools.Method(typeof(AkSoundEngine), nameof(AkSoundEngine.IsInitialized));
                var replacement = AccessTools.Method(typeof(WwiseAutomationPatches), nameof(IsInitializedForStartup));
                foreach (var instruction in instructions)
                {
                    if (instruction.Calls(original)) instruction.operand = replacement;
                    yield return instruction;
                }
            }
        }

        [HarmonyPatch]
        private static class AudioPackagePatches
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var name in new[]
                         {
                             nameof(AudioFilePackagesSettings.LoadPackagesChunk),
                             nameof(AudioFilePackagesSettings.UnloadPackagesChunk),
                             nameof(AudioFilePackagesSettings.LoadBanksChunk),
                             nameof(AudioFilePackagesSettings.UnloadBanksChunk)
                         })
                {
                    yield return AccessTools.Method(typeof(AudioFilePackagesSettings), name);
                }
            }

            private static bool Prefix()
            {
                return !IsAudioDisabledForAutomation();
            }
        }

        [HarmonyPatch(typeof(SoundBanksManager), nameof(SoundBanksManager.LoadBank), typeof(string))]
        [HarmonyPrefix]
        private static bool LoadSoundBankPrefix(ref SoundBanksManager.BankHandle __result)
        {
            if (!IsAudioDisabledForAutomation())
                return true;

            __result = null;
            return false;
        }

        [HarmonyPatch]
        private static class SoundManagerVoidPatches
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(SoundBanksManager), nameof(SoundBanksManager.LoadBankSync));
                yield return AccessTools.Method(typeof(SoundBanksManager), nameof(SoundBanksManager.LoadBanks));
                yield return AccessTools.Method(typeof(SoundPackagesManager), nameof(SoundPackagesManager.LoadPackage));
                yield return AccessTools.Method(typeof(SoundPackagesManager), nameof(SoundPackagesManager.UnloadPackage));
            }

            private static bool Prefix()
            {
                return !IsAudioDisabledForAutomation();
            }
        }

        [HarmonyPatch]
        private static class AkResultPatches
        {
            private static IEnumerable<MethodBase> TargetMethods() => AkMethodsReturning(typeof(AKRESULT));
            private static bool Prefix(ref AKRESULT __result)
            {
                if (!IsAudioDisabledForAutomation()) return true;
                __result = AKRESULT.AK_Success;
                return false;
            }
        }

        [HarmonyPatch]
        private static class AkVoidPatches
        {
            private static IEnumerable<MethodBase> TargetMethods() => AkMethodsReturning(typeof(void));
            private static bool Prefix() => !IsAudioDisabledForAutomation();
        }

        [HarmonyPatch]
        private static class AkBoolPatches
        {
            private static IEnumerable<MethodBase> TargetMethods() => AkMethodsReturning(typeof(bool));
            private static bool Prefix(ref bool __result)
            {
                if (!IsAudioDisabledForAutomation()) return true;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch]
        private static class AkUIntPatches
        {
            private static IEnumerable<MethodBase> TargetMethods() => AkMethodsReturning(typeof(uint));
            private static bool Prefix(ref uint __result)
            {
                if (!IsAudioDisabledForAutomation()) return true;
                __result = 0;
                return false;
            }
        }

        [HarmonyPatch]
        private static class AkULongPatches
        {
            private static IEnumerable<MethodBase> TargetMethods() => AkMethodsReturning(typeof(ulong));
            private static bool Prefix(ref ulong __result)
            {
                if (!IsAudioDisabledForAutomation()) return true;
                __result = 0;
                return false;
            }
        }

        [HarmonyPatch]
        private static class AkBytePatches
        {
            private static IEnumerable<MethodBase> TargetMethods() => AkMethodsReturning(typeof(byte));
            private static bool Prefix(ref byte __result)
            {
                if (!IsAudioDisabledForAutomation()) return true;
                __result = 0;
                return false;
            }
        }

        [HarmonyPatch]
        private static class AkUShortPatches
        {
            private static IEnumerable<MethodBase> TargetMethods() => AkMethodsReturning(typeof(ushort));
            private static bool Prefix(ref ushort __result)
            {
                if (!IsAudioDisabledForAutomation()) return true;
                __result = 0;
                return false;
            }
        }

        [HarmonyPatch]
        private static class AkIntPatches
        {
            private static IEnumerable<MethodBase> TargetMethods() => AkMethodsReturning(typeof(int));
            private static bool Prefix(ref int __result)
            {
                if (!IsAudioDisabledForAutomation()) return true;
                __result = 0;
                return false;
            }
        }

        [HarmonyPatch]
        private static class AkFloatPatches
        {
            private static IEnumerable<MethodBase> TargetMethods() => AkMethodsReturning(typeof(float));
            private static bool Prefix(ref float __result)
            {
                if (!IsAudioDisabledForAutomation()) return true;
                __result = 0f;
                return false;
            }
        }

        [HarmonyPatch]
        private static class AkStringPatches
        {
            private static IEnumerable<MethodBase> TargetMethods() => AkMethodsReturning(typeof(string));
            private static bool Prefix(ref string __result)
            {
                if (!IsAudioDisabledForAutomation()) return true;
                __result = string.Empty;
                return false;
            }
        }

        [HarmonyPatch]
        private static class AkChannelConfigPatches
        {
            private static IEnumerable<MethodBase> TargetMethods() => AkMethodsReturning(typeof(AkChannelConfig));
            private static bool Prefix(ref AkChannelConfig __result)
            {
                if (!IsAudioDisabledForAutomation()) return true;
                __result = null;
                return false;
            }
        }

        [HarmonyPatch]
        private static class AkPlaylistPatches
        {
            private static IEnumerable<MethodBase> TargetMethods() => AkMethodsReturning(typeof(AkPlaylist));
            private static bool Prefix(ref AkPlaylist __result)
            {
                if (!IsAudioDisabledForAutomation()) return true;
                __result = null;
                return false;
            }
        }

    }
}
