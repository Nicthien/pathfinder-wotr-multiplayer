using System;
using System.IO;

namespace WOTRMultiplayer.Services.GameInteraction
{
    internal static class RealTimeNativeStashPaths
    {
        // Native LoadRoutine clears this directory recursively. Verify the
        // actual process-local Areas_<pid> directory BEFORE it can do so.
        internal static string ValidateRoot(string root, string persistentRoot, int processId)
        {
            if (processId <= 0 || string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(persistentRoot))
                throw new InvalidDataException("native-load-invalid-stash-root");
            var expected = Path.GetFullPath(Path.Combine(persistentRoot, "Areas_" + processId)).TrimEnd(Path.DirectorySeparatorChar);
            var actual = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("native-load-stash-is-not-current-process");
            RejectLinks(actual);
            if (!Directory.Exists(actual) || Directory.GetDirectories(actual).Length != 0)
                throw new InvalidDataException("native-load-stash-layout-unsupported");
            foreach (var file in Directory.GetFiles(actual)) RejectLinks(file);
            return actual;
        }
        internal static string Target(string root, string name)
        {
            RealTimeMemorySaver.ValidateFileName(name);
            if (name.Equals("lock", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("native-load-stash-lock-forbidden");
            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!string.Equals(Path.GetDirectoryName(target), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("native-load-stash-target-escaped");
            RejectLinks(target);
            return target;
        }
        private static void RejectLinks(string path)
        {
            for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                // GetAttributes also detects a dangling reparse entry, unlike
                // File.Exists/Directory.Exists, which can follow its target.
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("native-load-stash-reparse-point-forbidden");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
    }
}
