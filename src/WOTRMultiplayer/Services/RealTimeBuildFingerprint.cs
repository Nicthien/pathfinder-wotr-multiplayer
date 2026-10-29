using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WOTRMultiplayer.Services
{
    /// <summary>Identifies the shipped RTwP code and dependencies, not mutable player settings.</summary>
    public static class RealTimeBuildFingerprint
    {
        private static readonly string[] RequiredFiles =
        [
            "WOTRMultiplayer.dll",
            "WOTRMultiplayer.Networking.dll",
            "WOTRMultiplayer.Localization.dll",
            "Info.json",
            Path.Combine("localization", "enGB.json"),
            Path.Combine("localization", "frFR.json")
        ];

        public static string Compute(string modFolder)
        {
            if (string.IsNullOrWhiteSpace(modFolder)) throw new ArgumentException("Mod folder is required", nameof(modFolder));
            foreach (var required in RequiredFiles)
                if (!File.Exists(Path.Combine(modFolder, required)))
                    throw new FileNotFoundException("Required RTwP payload is missing", Path.Combine(modFolder, required));

            var files = Directory.GetFiles(modFolder, "*.dll", SearchOption.TopDirectoryOnly);
            Array.Sort(files, (left, right) => StringComparer.Ordinal.Compare(
                Path.GetFileName(left), Path.GetFileName(right)));
            var manifest = new StringBuilder();
            using var sha = SHA256.Create();
            foreach (var path in files)
            {
                using var file = File.OpenRead(path);
                var fileHash = sha.ComputeHash(file);
                manifest.Append(Path.GetFileName(path)).Append(':')
                    .Append(BitConverter.ToString(fileHash).Replace("-", string.Empty)).Append('\n');
            }
            AppendFileHash(Path.Combine(modFolder, "Info.json"), "Info.json", sha, manifest);
            AppendFileHash(Path.Combine(modFolder, "localization", "enGB.json"), "localization/enGB.json", sha, manifest);
            AppendFileHash(Path.Combine(modFolder, "localization", "frFR.json"), "localization/frFR.json", sha, manifest);
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(manifest.ToString())))
                .Replace("-", string.Empty);
        }

        private static void AppendFileHash(string path, string relativePath, HashAlgorithm sha, StringBuilder manifest)
        {
            using var file = File.OpenRead(path);
            manifest.Append(relativePath).Append(':')
                .Append(BitConverter.ToString(sha.ComputeHash(file)).Replace("-", string.Empty)).Append('\n');
        }
    }
}
