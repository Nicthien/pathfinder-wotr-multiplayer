using System;
using System.IO;

namespace WOTRMultiplayer.Services.GameInteraction
{
    // Deliberately independent of AreaDataStash: even its FileName() triggers a
    // static initializer with filesystem side effects in an offline process.
    public static class RealTimeNativeCheckpointFiles
    {
        public static string AreaJson(string areaId, string sceneName)
        {
            if (!Guid.TryParseExact(areaId, "N", out _))
                throw new InvalidDataException("native-checkpoint-invalid-area-id");
            if (sceneName == null) throw new ArgumentNullException(nameof(sceneName));
            // Native AreaDataStash.CutSceneName is exactly a 62-character cut.
            var name = areaId + sceneName.Substring(0, Math.Min(sceneName.Length, 62)) + ".json";
            RealTimeMemorySaver.ValidateFileName(name);
            return name;
        }

        public static string Fog(string areaId, string sceneName)
        {
            if (!Guid.TryParseExact(areaId, "N", out _) || string.IsNullOrWhiteSpace(sceneName))
                throw new InvalidDataException("native-checkpoint-invalid-fog-identity");
            var name = areaId + "." + sceneName + ".fog";
            RealTimeMemorySaver.ValidateFileName(name);
            // Native loader splits on dots and reads segment 1 as scene name.
            if (sceneName.Contains(".")) throw new InvalidDataException("native-checkpoint-ambiguous-fog-scene");
            return name;
        }

        public static byte[] ReadBounded(Stream stream)
        {
            if (stream == null || !stream.CanRead || !stream.CanSeek)
                throw new InvalidDataException("native-checkpoint-unbounded-stash-stream");
            var length = stream.Length - stream.Position;
            if (length < 0 || length > RealTimeMemorySaver.MaximumFileBytes)
                throw new InvalidDataException("native-checkpoint-size-limit");
            var bytes = new byte[(int)length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) throw new EndOfStreamException("native-checkpoint-stash-truncated");
                offset += read;
            }
            if (stream.ReadByte() != -1) throw new InvalidDataException("native-checkpoint-stash-grew");
            return bytes;
        }
    }
}
