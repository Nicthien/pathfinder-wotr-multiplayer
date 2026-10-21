using System;
using System.IO;
using System.IO.Compression;
using WOTRMultiplayer.Services.GameInteraction;
using WOTRMultiplayer.Networking;
namespace WOTRMultiplayer.Services
{
    internal static class SessionRejoinRuntime
    {
        internal static object HostOwner;
        internal static object ClientOwner;
        internal static string ClientLoadPath;
        internal static object ClientSaveInfo;
        internal static bool Blocked => HostOwner != null || ClientOwner != null;
        internal static bool IsClientLoad(string path) => ClientOwner != null && ClientLoadPath != null && !string.IsNullOrWhiteSpace(path)
            && string.Equals(Path.GetFullPath(path), Path.GetFullPath(ClientLoadPath), StringComparison.OrdinalIgnoreCase);
        internal static byte[] Zip(RealTimeMemorySaver source)
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                foreach (var name in source.GetAllFiles())
                {
                    RealTimeMemorySaver.ValidateFileName(name);
                    using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                    var bytes = source.ReadBytes(name); entry.Write(bytes, 0, bytes.Length);
                    if (output.Length > RealTimeNativeCheckpointLimits.MaximumArchiveBytes)
                        throw new InvalidDataException("rejoin-archive-too-large");
                }
            var result = output.ToArray();
            if (result.Length > RealTimeNativeCheckpointLimits.MaximumArchiveBytes)
                throw new InvalidDataException("rejoin-archive-too-large");
            return result;
        }
    }
}
