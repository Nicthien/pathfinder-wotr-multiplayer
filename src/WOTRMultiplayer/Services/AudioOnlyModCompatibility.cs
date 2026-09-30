using System;

namespace WOTRMultiplayer.Services
{
    /// <summary>
    /// These two local voice mods do not add gameplay rules, units or save data.
    /// They may differ between players; every other enabled mod remains subject to strict matching.
    /// </summary>
    public static class AudioOnlyModCompatibility
    {
        public static bool IsLocalAudioMod(string modId)
        {
            return string.Equals(modId, "WOTR_AIVO", StringComparison.OrdinalIgnoreCase)
                || string.Equals(modId, "SpeechMod", StringComparison.OrdinalIgnoreCase);
        }
    }
}
