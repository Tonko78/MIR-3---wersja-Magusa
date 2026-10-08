using System;
using System.Collections.Generic;
using System.IO;

namespace Launcher.Core
{
    /// <summary>
    /// Binary patch manifest (PList.Bin) entry. The on-disk layout is unchanged
    /// from the original Zircon launcher and PatchManager pair:
    /// string fileName, int64 compressedLength, int32 checkSumLength, checkSum bytes.
    /// </summary>
    /// Not sealed: PatchManager's PatchInformation derives from this type to
    /// add upload-staging paths while reusing the shared wire format and
    /// validation.
    public class PatchManifestEntry
    {
        public string FileName { get; set; }
        public long CompressedLength { get; set; }
        public byte[] CheckSum { get; set; }

        public PatchManifestEntry()
        {
        }

        public PatchManifestEntry(BinaryReader reader)
        {
            FileName = reader.ReadString();
            CompressedLength = reader.ReadInt64();

            int length = reader.ReadInt32();

            if (length < 0 || length > PatchManifest.MaxCheckSumLength)
                throw new InvalidDataException($"Manifest entry has an invalid check sum length ({length}).");

            CheckSum = reader.ReadBytes(length);

            if (CheckSum.Length != length)
                throw new InvalidDataException("Manifest entry check sum is truncated.");
        }

        public void Save(BinaryWriter writer)
        {
            writer.Write(FileName ?? string.Empty);
            writer.Write(CompressedLength);
            writer.Write(CheckSum?.Length ?? 0);
            if (CheckSum != null)
                writer.Write(CheckSum);
        }

        public bool IsValid(out string error)
        {
            if (!PatchPaths.IsSafeRelativePath(FileName))
            {
                error = "the file name is missing or unsafe";
                return false;
            }

            if (CompressedLength < 0)
            {
                error = "the compressed length is negative";
                return false;
            }

            if (CheckSum == null || CheckSum.Length == 0 || CheckSum.Length > PatchManifest.MaxCheckSumLength)
            {
                error = "the check sum is missing or has an invalid length";
                return false;
            }

            error = null;
            return true;
        }
    }

    public static class PatchManifest
    {
        public const string FileName = "PList.Bin";
        public const int MaxCheckSumLength = 1024;
        public const int MaxEntries = 200000;
        public const int MaxManifestBytes = 64 * 1024 * 1024;

        public static List<PatchManifestEntry> Read(BinaryReader reader)
        {
            // HTTP response streams do not support Position or Length. Buffer
            // only the bounded manifest, never the (potentially large) payloads.
            if (!reader.BaseStream.CanSeek)
            {
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[8192];
                int count;
                while ((count = reader.BaseStream.Read(chunk, 0, chunk.Length)) != 0)
                {
                    if (buffer.Length + count > MaxManifestBytes)
                        throw new InvalidDataException("Patch manifest is too large.");
                    buffer.Write(chunk, 0, count);
                }
                buffer.Position = 0;
                using var bufferedReader = new BinaryReader(buffer, System.Text.Encoding.UTF8, leaveOpen: true);
                return Read(bufferedReader);
            }

            if (reader.BaseStream.Length - reader.BaseStream.Position > MaxManifestBytes)
                throw new InvalidDataException("Patch manifest is too large.");

            List<PatchManifestEntry> list = new List<PatchManifestEntry>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var payloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                if (list.Count >= MaxEntries)
                    throw new InvalidDataException($"Patch manifest has more than {MaxEntries} entries.");

                PatchManifestEntry entry = new PatchManifestEntry(reader);

                if (!entry.IsValid(out string error))
                    throw new InvalidDataException($"Invalid patch manifest entry '{entry.FileName}': {error}.");

                if (!names.Add(entry.FileName.Replace('/', '\\')) ||
                    !payloads.Add(PatchPaths.ToWebFileName(entry.FileName)))
                    throw new InvalidDataException($"Duplicate patch path or payload: '{entry.FileName}'.");

                list.Add(entry);
            }

            return list;
        }

        public static void Write(BinaryWriter writer, IEnumerable<PatchManifestEntry> entries)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var payloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PatchManifestEntry entry in entries)
            {
                if (!entry.IsValid(out string error))
                    throw new ArgumentException($"Invalid patch manifest entry '{entry.FileName}': {error}.", nameof(entries));

                if (!names.Add(entry.FileName.Replace('/', '\\')) ||
                    !payloads.Add(PatchPaths.ToWebFileName(entry.FileName)))
                    throw new ArgumentException($"Duplicate patch path or payload: '{entry.FileName}'.", nameof(entries));

                entry.Save(writer);
            }
        }

        public static bool IsMatch(byte[] a, byte[] b, long offSet = 0)
        {
            if (b == null || a == null || b.Length + offSet > a.Length || offSet < 0) return false;

            for (int i = 0; i < b.Length; i++)
                if (a[offSet + i] != b[i])
                    return false;

            return true;
        }
    }
}
