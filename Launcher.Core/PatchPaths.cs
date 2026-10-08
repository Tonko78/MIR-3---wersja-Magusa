using System;
using System.IO;

namespace Launcher.Core
{
    /// <summary>
    /// Guards every path that comes from a patch manifest before it touches the
    /// local file system. Manifest entries must be relative, must stay inside
    /// the client directory, and map to exactly one web payload name.
    /// </summary>
    public static class PatchPaths
    {
        public const int MaxFileNameLength = 260;

        public static bool IsLocalState(string fileName)
        {
            if (!IsSafeRelativePath(fileName)) return false;
            string normalized = fileName.Replace('/', '\\');
            return normalized.Equals("Zircon.ini", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Equals("Data\\Users.db", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSafeRelativePath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > MaxFileNameLength)
                return false;

            // Path.IsPathRooted covers "/..." on every platform; the ':' check
            // additionally rejects Windows drive letters and alternate streams.
            if (Path.IsPathRooted(fileName) || fileName.IndexOf(':') >= 0)
                return false;

            if (fileName.EndsWith("/", StringComparison.Ordinal) || fileName.EndsWith("\\", StringComparison.Ordinal))
                return false;

            string[] segments = fileName.Split(new[] { '/', '\\' }, StringSplitOptions.None);

            foreach (string segment in segments)
            {
                if (segment.Length == 0)
                    return false;

                if (string.Equals(segment, ".", StringComparison.Ordinal) ||
                    string.Equals(segment, "..", StringComparison.Ordinal))
                    return false;

                if (segment.EndsWith(".", StringComparison.Ordinal) ||
                    segment.EndsWith(" ", StringComparison.Ordinal))
                    return false;

                string device = segment.Split('.')[0];
                if (device.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                    device.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                    device.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                    device.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                    (device.Length == 4 &&
                     (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                      device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                     device[3] >= '1' && device[3] <= '9'))
                    return false;

                foreach (char c in segment)
                {
                    if (c < 32 || c == '?' || c == '*' || c == '|' ||
                        c == '<' || c == '>' || c == '"')
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Maps a manifest file name to its compressed web payload name.
        /// Directory separators become '-' so every payload is a flat file name.
        /// </summary>
        public static string ToWebFileName(string fileName)
        {
            if (!IsSafeRelativePath(fileName))
                throw new ArgumentException($"Unsafe patch file name: '{fileName}'.", nameof(fileName));

            return fileName.Replace('\\', '-').Replace('/', '-') + ".gz";
        }

        /// <summary>
        /// Resolves a manifest file name against the client root and guarantees
        /// the result stays inside that root, regardless of separator style.
        /// </summary>
        public static string ResolveClientPath(string clientRoot, string fileName)
        {
            if (!IsSafeRelativePath(fileName))
                throw new ArgumentException($"Unsafe patch file name: '{fileName}'.", nameof(fileName));

            string normalized = fileName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            string rootFull = Path.GetFullPath(clientRoot);
            string candidate = Path.GetFullPath(Path.Combine(rootFull, normalized));
            string rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;

            if (!candidate.StartsWith(rootWithSeparator, StringComparison.Ordinal))
                throw new ArgumentException($"Patch file '{fileName}' escapes the client directory.", nameof(fileName));

            // A lexical containment check is not enough when a client folder
            // already contains a junction or symlink pointing outside it.
            string current = rootFull;
            RejectReparsePoint(current, fileName);
            foreach (string segment in normalized.Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                RejectReparsePoint(current, fileName);
            }

            return candidate;
        }

        private static void RejectReparsePoint(string path, string fileName)
        {
            if (new FileInfo(path).LinkTarget != null)
                throw new ArgumentException($"Patch file '{fileName}' traverses a reparse point.", nameof(fileName));
        }
    }
}
