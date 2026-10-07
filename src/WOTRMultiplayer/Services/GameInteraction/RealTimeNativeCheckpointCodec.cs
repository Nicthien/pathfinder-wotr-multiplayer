using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Bounded, uncompressed, deterministic internal archive. It never executes
    // JSON types or creates ZipSaver/player-save files. Network authentication,
    // session/epoch binding and native graph loading are separate requirements.
    public static class RealTimeNativeCheckpointCodec
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RTWNCP01");
        private static readonly UTF8Encoding Utf8 = new(false, true);
        public const int MaximumArchiveBytes = RealTimeMemorySaver.MaximumTotalBytes + 4 * 1024 * 1024;

        public static string Hash(byte[] archive)
        {
            if (archive == null || archive.Length > MaximumArchiveBytes) throw new InvalidDataException("native-checkpoint-archive-size-limit");
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(archive)).Replace("-", "");
        }

        public static byte[] Encode(RealTimeMemorySaver source)
        {
            if (source == null || !source.IsSealed) throw new InvalidOperationException("native-checkpoint-source-not-sealed");
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Utf8, true);
            writer.Write(Magic);
            var names = source.GetAllFiles(); writer.Write(names.Count);
            foreach (var name in names)
            {
                var encodedName = Utf8.GetBytes(name); var content = source.ReadBytes(name);
                writer.Write(encodedName.Length); writer.Write(encodedName);
                writer.Write(content.Length); writer.Write(content);
                if (stream.Length > MaximumArchiveBytes) throw new InvalidDataException("native-checkpoint-archive-size-limit");
            }
            writer.Flush(); return stream.ToArray();
        }

        private static byte[] ReadExact(BinaryReader reader, int length)
        {
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("native-checkpoint-truncated-archive");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new InvalidDataException("native-checkpoint-truncated-archive");
            return bytes;
        }

        public static RealTimeMemorySaver Decode(byte[] archive, string expectedHash,
            Action<string, byte[]> writeStash = null)
        {
            if (archive == null || archive.Length > MaximumArchiveBytes)
                throw new InvalidDataException("native-checkpoint-archive-size-limit");
            // Own the bytes before hashing. The caller cannot change an entry
            // between integrity verification and native-source construction.
            archive = (byte[])archive.Clone();
            if (expectedHash == null || expectedHash.Length != 64
                || !expectedHash.All(character => character >= '0' && character <= '9' || character >= 'A' && character <= 'F')
                || Hash(archive) != expectedHash)
                throw new InvalidDataException("native-checkpoint-archive-hash-mismatch");
            var result = new RealTimeMemorySaver(writeStash: writeStash);
            try
            {
                using var stream = new MemoryStream(archive, false);
                using var reader = new BinaryReader(stream, Utf8, true);
                if (!ReadExact(reader, Magic.Length).SequenceEqual(Magic))
                    throw new InvalidDataException("native-checkpoint-unsupported-archive");
                var count = reader.ReadInt32();
                if (count < 3 || count > RealTimeMemorySaver.MaximumFiles)
                    throw new InvalidDataException("native-checkpoint-file-count-limit");
                string previous = null;
                long total = 0;
                for (var i = 0; i < count; i++)
                {
                    var nameLength = reader.ReadInt32();
                    if (nameLength < 1 || nameLength > 800) throw new InvalidDataException("native-checkpoint-file-name-limit");
                    var name = Utf8.GetString(ReadExact(reader, nameLength));
                    RealTimeMemorySaver.ValidateFileName(name);
                    // Require canonical order too; there is only one encoding
                    // for a validated map, not arbitrary reordered aliases.
                    if (previous != null && string.CompareOrdinal(previous, name) >= 0)
                        throw new InvalidDataException("native-checkpoint-file-order-or-duplicate");
                    var length = reader.ReadInt32(); total += length;
                    if (length < 0 || length > RealTimeMemorySaver.MaximumFileBytes || total > RealTimeMemorySaver.MaximumTotalBytes)
                        throw new InvalidDataException("native-checkpoint-size-limit");
                    result.SaveBytes(name, ReadExact(reader, length)); previous = name;
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("native-checkpoint-trailing-content");
                result.Seal(); return result;
            }
            catch (EndOfStreamException failure)
            { result.Dispose(); throw new InvalidDataException("native-checkpoint-truncated-archive", failure); }
            catch { result.Dispose(); throw; }
        }
    }
}
