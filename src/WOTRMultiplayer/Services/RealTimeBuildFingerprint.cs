using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WOTRMultiplayer.Services
{
    /// <summary>Identifies the installed RTwP code and French UI payload, not mutable player settings.</summary>
    public static class RealTimeBuildFingerprint
    {
        private static readonly string[] Files =
        [
            "WOTRMultiplayer.dll",
            "WOTRMultiplayer.Networking.dll",
            "WOTRMultiplayer.Localization.dll",
            Path.Combine("localization", "enGB.json"),
            Path.Combine("localization", "frFR.json")
        ];

        public static string Compute(string modFolder)
        {
            if (string.IsNullOrWhiteSpace(modFolder)) throw new ArgumentException("Mod folder is required", nameof(modFolder));
            var manifest = new StringBuilder();
            using var sha = SHA256.Create();
            foreach (var relativePath in Files)
            {
                var path = Path.Combine(modFolder, relativePath);
                using var file = File.OpenRead(path);
                var fileHash = sha.ComputeHash(file);
                manifest.Append(relativePath.Replace('\\', '/')).Append(':')
                    .Append(BitConverter.ToString(fileHash).Replace("-", string.Empty)).Append('\n');
            }
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(manifest.ToString())))
                .Replace("-", string.Empty);
        }
    }
}
