using System;
using WOTRMultiplayer.Entities.Content;

namespace WOTRMultiplayer.Services
{
    [Flags]
    public enum RealTimeContentMismatch
    {
        None = 0,
        GameVersion = 1,
        ProtocolVersion = 2,
        BuildFingerprint = 4
    }

    public static class RealTimeContentCompatibility
    {
        public static RealTimeContentMismatch Compare(NetworkContentState host, NetworkContentState client)
        {
            if (host == null || client == null)
                return RealTimeContentMismatch.GameVersion
                    | RealTimeContentMismatch.ProtocolVersion
                    | RealTimeContentMismatch.BuildFingerprint;

            var mismatch = RealTimeContentMismatch.None;
            if (string.IsNullOrWhiteSpace(host.GameVersion)
                || !string.Equals(host.GameVersion, client.GameVersion, StringComparison.OrdinalIgnoreCase))
                mismatch |= RealTimeContentMismatch.GameVersion;
            if (host.RealTimeProtocolVersion != Entities.Combat.RealTime.NetworkRealTimeCombatState.ProtocolVersion
                || host.RealTimeProtocolVersion != client.RealTimeProtocolVersion)
                mismatch |= RealTimeContentMismatch.ProtocolVersion;
            if (string.IsNullOrWhiteSpace(host.RealTimeBuildFingerprint)
                || !string.Equals(host.RealTimeBuildFingerprint, client.RealTimeBuildFingerprint,
                    StringComparison.OrdinalIgnoreCase))
                mismatch |= RealTimeContentMismatch.BuildFingerprint;
            return mismatch;
        }
    }
}
