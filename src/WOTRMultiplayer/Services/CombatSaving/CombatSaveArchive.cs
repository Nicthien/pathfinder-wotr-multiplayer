using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WOTRMultiplayer.Services.GameInteraction;
using WOTRMultiplayer.Services.Random;

namespace WOTRMultiplayer.Services.CombatSaving
{
    internal sealed class CombatSaveManifest
    {
        public int Schema { get; set; } = 1;
        public int Protocol { get; set; }
        public string BuildFingerprint { get; set; }
        public string GameVersion { get; set; }
        public string Campaign { get; set; }
        public int Round { get; set; }
        public string Unit { get; set; }
        public string CanonicalDigest { get; set; }
        public string RestoreDigest { get; set; }
        public Dictionary<string, long> Owners { get; set; } = new();
        public Dictionary<string, bool> OwnerIsHost { get; set; } = new();
        public string NetworkCombatJson { get; set; }
        public CombatSaveNativeSupplement NativeSupplement { get; set; }
        public Dictionary<string, int> Seeds { get; set; } = new();
        public DeterministicGeneratorCheckpoint Generators { get; set; }
        public Dictionary<string, string> Files { get; set; } = new();
    }

    // Storage primitive only. Does not capture a live world, bypass SaveManager,
    // call LoadGame, register a save slot or enable the feature.
    internal static class CombatSaveArchive
    {
        // Unknown *.json entries are treated as area files by the native loader.
        internal const string ManifestName = "multiplayer-combat.state";
        private static readonly string[] Required = { "header.json", "player.json", "party.json", "tbm.json" };
        private static readonly JsonSerializerSettings Settings = new()
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MaxDepth = 128
        };

        internal static string Hash(byte[] data)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
        }

        private static void ValidateManifest(CombatSaveManifest manifest)
        {
            if (manifest?.Schema != 1 || manifest.Protocol < 1 || manifest.Round < 0
                || string.IsNullOrWhiteSpace(manifest.Campaign) || string.IsNullOrWhiteSpace(manifest.Unit)
                || string.IsNullOrWhiteSpace(manifest.GameVersion) || !IsHash(manifest.BuildFingerprint)
                || !IsHash(manifest.CanonicalDigest) || !IsHash(manifest.RestoreDigest)
                || manifest.Generators?.Version != 1
                || manifest.Owners == null || manifest.Seeds == null || manifest.Files == null)
                throw new InvalidDataException("combat-save-metadata-invalid");
        }

        private static bool IsHash(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit);

        private static void ValidateNative(IReadOnlyDictionary<string, byte[]> files, string campaign)
        {
            if (Required.Any(name => !files.ContainsKey(name)))
                throw new InvalidDataException("combat-save-native-files-missing");
            // A blank RT checkpoint is explicitly not a turn-based save.
            foreach (var name in Required)
            {
                var text = new UTF8Encoding(false, true).GetString(files[name]);
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("combat-save-native-json-empty");
                RealTimeNativeCheckpointLoadSource.ValidateJson(text, name != "party.json");
                using var reader = new JsonTextReader(new StringReader(text)) { MaxDepth = 128 };
                var value = JToken.Load(reader);
                if (reader.Read()) throw new InvalidDataException("combat-save-native-json-trailing-data");
                if (name == "tbm.json" && (value is not JObject turn || !turn.Properties().Any()))
                    throw new InvalidDataException("combat-save-turn-state-empty");
                if (name == "header.json" && (string)value["GameId"] != campaign)
                    throw new InvalidDataException("combat-save-campaign-mismatch");
            }
        }

        internal static string WriteNew(string directory, RealTimeMemorySaver source, CombatSaveManifest manifest, DateTime localTime,
            Func<bool> mayPublish = null)
        {
            ValidateManifest(manifest);
            if (source?.IsSealed != true) throw new InvalidOperationException("combat-save-source-not-sealed");
            var files = source.GetAllFiles().ToDictionary(name => name, source.ReadBytes, StringComparer.OrdinalIgnoreCase);
            if (files.ContainsKey(ManifestName)) throw new InvalidDataException("combat-save-reserved-entry");
            ValidateNative(files, manifest.Campaign);
            if (manifest.NativeSupplement != null)
            {
                var turn = JObject.Parse(Encoding.UTF8.GetString(files["tbm.json"]));
                manifest.NativeSupplement.AddTo(turn);
                files["tbm.json"] = Encoding.UTF8.GetBytes(turn.ToString(Formatting.None));
            }
            // Do not mutate the caller's manifest while writing.
            var snapshot = JsonConvert.DeserializeObject<CombatSaveManifest>(JsonConvert.SerializeObject(manifest, Settings), Settings);
            snapshot.Files = files.ToDictionary(x => x.Key, x => Hash(x.Value), StringComparer.OrdinalIgnoreCase);
            var metadata = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(snapshot, Settings));
            var root = Path.GetFullPath(directory);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            var token = Guid.NewGuid().ToString("N");
            var name = "Multi - Combat - " + localTime.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + " - " + token + ".zks";
            var destination = Path.Combine(root, name);
            var temporary = Path.Combine(root, ".combat-save-" + token + ".tmp");
            var created = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    created = true;
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                    {
                        foreach (var file in files.OrderBy(x => x.Key, StringComparer.Ordinal)) Add(zip, file.Key, file.Value);
                        Add(zip, ManifestName, metadata);
                    }
                    stream.Flush(true);
                }
                ValidateFile(temporary, manifest.Protocol, manifest.BuildFingerprint, manifest.GameVersion);
                if (mayPublish != null && !mayPublish()) throw new InvalidOperationException("combat-save-publication-cancelled");
                File.Move(temporary, destination); // Same-volume publish; never overwrites.
                created = false;
                return destination;
            }
            finally { if (created) File.Delete(temporary); }
        }

        private static void Add(ZipArchive zip, string name, byte[] bytes)
        {
            RealTimeMemorySaver.ValidateFileName(name);
            using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            stream.Write(bytes, 0, bytes.Length);
        }

        internal static CombatSaveManifest ValidateFile(string path, int protocol, string fingerprint, string gameVersion,
            IReadOnlyCollection<string> compatibleFingerprints = null)
        {
            using var stream = File.OpenRead(path);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            if (zip.Entries.Count > RealTimeMemorySaver.MaximumFiles + 1)
                throw new InvalidDataException("combat-save-too-many-files");
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                RealTimeMemorySaver.ValidateFileName(entry.FullName);
                total += entry.Length;
                if (entry.Length > RealTimeMemorySaver.MaximumFileBytes || total > RealTimeMemorySaver.MaximumTotalBytes
                    || files.ContainsKey(entry.FullName)) throw new InvalidDataException("combat-save-invalid-archive");
                using var input = entry.Open();
                var bytes = new byte[(int)entry.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    var count = input.Read(bytes, read, bytes.Length - read);
                    if (count == 0) throw new EndOfStreamException();
                    read += count;
                }
                if (input.ReadByte() != -1) throw new InvalidDataException("combat-save-entry-length-mismatch");
                files.Add(entry.FullName, bytes);
            }
            if (!files.TryGetValue(ManifestName, out var metadata)) throw new InvalidDataException("combat-save-metadata-missing");
            var metadataJson = new UTF8Encoding(false, true).GetString(metadata);
            RealTimeNativeCheckpointLoadSource.ValidateJson(metadataJson, true);
            var manifest = JsonConvert.DeserializeObject<CombatSaveManifest>(metadataJson, Settings);
            ValidateManifest(manifest);
            var fingerprintMatches = string.Equals(manifest.BuildFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
                || compatibleFingerprints?.Any(value => string.Equals(manifest.BuildFingerprint, value,
                    StringComparison.OrdinalIgnoreCase)) == true;
            if (manifest.Protocol != protocol || !fingerprintMatches || manifest.GameVersion != gameVersion)
                throw new InvalidDataException("combat-save-incompatible-build");
            files.Remove(ManifestName);
            if (manifest.Files.Count != files.Count || files.Any(x => !manifest.Files.TryGetValue(x.Key, out var hash) || Hash(x.Value) != hash))
                throw new InvalidDataException("combat-save-integrity-failed");
            ValidateNative(files, manifest.Campaign);
            return manifest;
        }

        internal static bool ContainsManifest(string path)
        {
            using var stream = File.OpenRead(path);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            return zip.Entries.Any(e => e.FullName == ManifestName);
        }

        internal static string ReadNativeTurn(string path)
        {
            using var stream = File.OpenRead(path);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.GetEntry("tbm.json") ?? throw new InvalidDataException("combat-save-turn-missing");
            if (entry.Length > GameInteraction.RealTimeMemorySaver.MaximumFileBytes)
                throw new InvalidDataException("combat-save-turn-too-large");
            using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false, true));
            return reader.ReadToEnd();
        }
    }
}
