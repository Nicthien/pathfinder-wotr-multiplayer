using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Kingmaker.EntitySystem.Persistence;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Native loader source, not a player save slot. Every byte remains in RAM.
    // Stash access is a separate capability; no implicit filesystem access.
    public sealed class RealTimeMemorySaver : ISaver
    {
        public const int MaximumFiles = 16384;
        public const int MaximumFileBytes = 64 * 1024 * 1024;
        public const int MaximumTotalBytes = 128 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, byte[]> _readStash;
        private readonly Action<string, byte[]> _writeStash;
        private long _bytes;
        private bool _disposed;
        private object _nativeLoadOwner;
        public bool IsSealed { get; private set; }
        public long TotalBytes { get { EnsureAlive(); return _bytes; } }

        public RealTimeMemorySaver(Func<string, byte[]> readStash = null, Action<string, byte[]> writeStash = null)
        { _readStash = readStash; _writeStash = writeStash; }

        public static void ValidateFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name != name.Normalize(NormalizationForm.FormC)
                || name != name.Trim() || name.EndsWith(".", StringComparison.Ordinal)
                || name == "." || name == ".." || name.IndexOfAny("\\/:*?\"<>|".ToCharArray()) >= 0
                || name.Any(character => char.IsControl(character)
                    || char.GetUnicodeCategory(character) == UnicodeCategory.Format))
                throw new InvalidDataException("native-checkpoint-invalid-file-name");
            var stem = name.Split('.')[0].TrimEnd(' ');
            if (new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$" }
                    .Contains(stem, StringComparer.OrdinalIgnoreCase)
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && "123456789¹²³".Contains(stem[3]))
                throw new InvalidDataException("native-checkpoint-reserved-file-name");
        }

        private void EnsureAlive()
        { if (_disposed) throw new ObjectDisposedException(nameof(RealTimeMemorySaver)); }
        private void EnsureMutable()
        {
            EnsureAlive();
            if (IsSealed) throw new InvalidOperationException("native-checkpoint-source-is-read-only");
        }

        public string ReadHeader() => ReadJson("header");
        public string ReadJson(string name)
        {
            var content = ReadBytes(name + ".json");
            if (content == null) return null;
            // Match StreamReader's ordinary UTF-8 BOM handling, but do not
            // silently replace damaged UTF-8 in a mechanical checkpoint.
            var offset = content.Length >= 3 && content[0] == 0xef && content[1] == 0xbb && content[2] == 0xbf ? 3 : 0;
            return Utf8.GetString(content, offset, content.Length - offset);
        }
        public byte[] ReadBytes(string name)
        {
            EnsureAlive(); ValidateFileName(name);
            return _files.TryGetValue(name, out var content) ? (byte[])content.Clone() : null;
        }
        public void SaveJson(string name, string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            SaveBytes(name + ".json", Utf8.GetBytes(json));
        }
        public void SaveBytes(string name, byte[] bytes)
        {
            EnsureMutable(); ValidateFileName(name);
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            var exists = _files.TryGetValue(name, out var previous);
            // Windows aliases must not silently overwrite a different archive
            // entry. Exact-name replacement is reserved for host live overlays.
            if (exists && !_files.Keys.Any(key => string.Equals(key, name, StringComparison.Ordinal)))
                throw new InvalidDataException("native-checkpoint-file-name-alias");
            var total = _bytes - (previous?.Length ?? 0) + bytes.Length;
            if (bytes.Length > MaximumFileBytes || total > MaximumTotalBytes
                || !exists && _files.Count == MaximumFiles)
                throw new InvalidDataException("native-checkpoint-size-limit");
            var owned = (byte[])bytes.Clone();
            _files[name] = owned; _bytes = total;
        }

        public void CopyFromStash(string fileName)
        {
            EnsureMutable(); ValidateFileName(fileName);
            if (_readStash == null) throw new InvalidOperationException("native-checkpoint-stash-read-not-authorized");
            var content = _readStash(fileName) ?? throw new FileNotFoundException("native-checkpoint-stash-file-missing", fileName);
            SaveBytes(fileName, content);
        }
        public void CopyToStash(string fileName)
        {
            EnsureAlive(); ValidateFileName(fileName);
            if (!IsSealed || _writeStash == null)
                throw new InvalidOperationException("native-checkpoint-stash-write-not-authorized");
            var content = ReadBytes(fileName) ?? throw new FileNotFoundException("native-checkpoint-file-missing", fileName);
            _writeStash(fileName, content);
        }

        public void Seal()
        {
            EnsureAlive();
            foreach (var name in new[] { "header", "player", "party" })
                if (string.IsNullOrWhiteSpace(ReadJson(name)))
                    throw new InvalidDataException("native-checkpoint-required-document-missing:" + name);
            IsSealed = true;
        }
        public void Clear() { EnsureMutable(); _files.Clear(); _bytes = 0; }
        public void Save() => throw new InvalidOperationException("native-checkpoint-cannot-write-player-save");
        public List<string> GetAllFiles()
        { EnsureAlive(); return _files.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList(); }
        public ISaver Clone()
        {
            EnsureAlive();
            var clone = new RealTimeMemorySaver(_readStash, _writeStash);
            clone._nativeLoadOwner = _nativeLoadOwner;
            foreach (var file in _files)
                if (IsSealed) clone._files.Add(file.Key, file.Value); // Immutable, never exposed without copying.
                else clone.SaveBytes(file.Key, file.Value);
            if (IsSealed) { clone._bytes = _bytes; clone.IsSealed = true; }
            return clone;
        }

        internal bool IsNativeLoadSource(object owner) => !_disposed && IsSealed
            && owner != null && ReferenceEquals(_nativeLoadOwner, owner);

        internal RealTimeMemorySaver WithNativeStashWriter(object owner, Action<string, byte[]> write)
        {
            EnsureAlive();
            if (!IsSealed || owner == null || write == null)
                throw new InvalidOperationException("native-checkpoint-stash-capability-invalid");
            var result = new RealTimeMemorySaver(writeStash: write) { _nativeLoadOwner = owner };
            foreach (var file in _files) result._files.Add(file.Key, file.Value);
            result._bytes = _bytes; result.IsSealed = true;
            return result;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _files.Clear(); _bytes = 0; _disposed = true;
        }

        public static RealTimeMemorySaver CopySource(ISaver source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var result = new RealTimeMemorySaver();
            try
            {
                // Never Clear/Save/Dispose the caller's original source.
                using (var snapshot = source.Clone())
                    foreach (var name in snapshot.GetAllFiles())
                    {
                        if (result._files.ContainsKey(name)) throw new InvalidDataException("native-checkpoint-duplicate-source-file");
                        result.SaveBytes(name, snapshot.ReadBytes(name)
                            ?? throw new InvalidDataException("native-checkpoint-source-file-disappeared"));
                    }
                return result;
            }
            catch { result.Dispose(); throw; }
        }
    }
}
