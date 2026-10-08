using System.IO.Compression;
using System.Security.Cryptography;
using Launcher.Core;

namespace Launcher.Core.Tests;

public sealed class PatchPayloadTests
{
    [Fact]
    public async Task Valid_gzip_replaces_existing_file_after_hash_verification()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mir3-payload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string target = Path.Combine(directory, "Zircon.exe");
            await File.WriteAllTextAsync(target, "old");
            byte[] content = "new game binary"u8.ToArray();
            using var compressed = Compress(content);

            await PatchPayload.ExtractVerifiedAsync(compressed, target, MD5.HashData(content));

            Assert.Equal(content, await File.ReadAllBytesAsync(target));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Bad_checksum_preserves_old_file_and_removes_temporary_file()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mir3-payload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string target = Path.Combine(directory, "Zircon.exe");
            await File.WriteAllTextAsync(target, "old");
            using var compressed = Compress("different content"u8.ToArray());

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                PatchPayload.ExtractVerifiedAsync(compressed, target, new byte[16]));

            Assert.Equal("old", await File.ReadAllTextAsync(target));
            Assert.Equal(new[] { "Zircon.exe" }, Directory.GetFiles(directory).Select(Path.GetFileName));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Truncated_gzip_preserves_old_file()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mir3-payload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string target = Path.Combine(directory, "Zircon.exe");
            await File.WriteAllTextAsync(target, "old");
            using var compressed = Compress("new"u8.ToArray());
            compressed.SetLength(compressed.Length - 5);
            compressed.Position = 0;

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                PatchPayload.ExtractVerifiedAsync(compressed, target, MD5.HashData("new"u8.ToArray())));

            Assert.Equal("old", await File.ReadAllTextAsync(target));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static MemoryStream Compress(byte[] content)
    {
        var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionMode.Compress, leaveOpen: true))
            gzip.Write(content);
        stream.Position = 0;
        return stream;
    }
}
