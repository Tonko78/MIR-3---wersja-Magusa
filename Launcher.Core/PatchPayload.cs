using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Launcher.Core
{
    /// <summary>Extracts a patch into a sibling temporary file and replaces the
    /// target only after both the gzip trailer and manifest MD5 are verified.</summary>
    public static class PatchPayload
    {
        private const long MaxExtractedBytes = 4L * 1024 * 1024 * 1024;
        private static readonly uint[] CrcTable = CreateCrcTable();

        public static async Task ExtractVerifiedAsync(Stream compressed, string targetPath, byte[] expectedMd5)
        {
            if (compressed == null || !compressed.CanRead || !compressed.CanSeek)
                throw new ArgumentException("A seekable compressed file is required.", nameof(compressed));
            if (expectedMd5 == null || expectedMd5.Length != 16)
                throw new ArgumentException("An MD5 manifest checksum is required.", nameof(expectedMd5));
            if (compressed.Length - compressed.Position < 18)
                throw new InvalidDataException("Compressed patch is too short.");

            long start = compressed.Position;
            compressed.Position = compressed.Length - 8;
            byte[] trailer = new byte[8];
            if (await compressed.ReadAsync(trailer, 0, trailer.Length) != trailer.Length)
                throw new InvalidDataException("Compressed patch trailer is truncated.");
            uint expectedCrc = BitConverter.ToUInt32(trailer, 0);
            uint expectedSize = BitConverter.ToUInt32(trailer, 4);
            compressed.Position = start;

            string fullTarget = Path.GetFullPath(targetPath);
            string parent = Path.GetDirectoryName(fullTarget);
            Directory.CreateDirectory(parent);
            string temporary = fullTarget + ".patch-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                uint crc = 0xffffffff;
                long written = 0;
                using (var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    byte[] buffer = new byte[81920];
                    int count;
                    while ((count = await gzip.ReadAsync(buffer, 0, buffer.Length)) != 0)
                    {
                        written += count;
                        if (written > MaxExtractedBytes)
                            throw new InvalidDataException("Extracted patch exceeds the size limit.");
                        hash.AppendData(buffer, 0, count);
                        for (int i = 0; i < count; i++)
                            crc = CrcTable[(crc ^ buffer[i]) & 0xff] ^ (crc >> 8);
                        await output.WriteAsync(buffer, 0, count);
                    }
                    await output.FlushAsync();
                }

                if (crc != ~expectedCrc || (uint)written != expectedSize ||
                    !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expectedMd5))
                    throw new InvalidDataException("Compressed patch failed checksum verification.");

                File.Move(temporary, fullTarget, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int j = 0; j < 8; j++)
                    value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1;
                table[i] = value;
            }
            return table;
        }
    }
}
