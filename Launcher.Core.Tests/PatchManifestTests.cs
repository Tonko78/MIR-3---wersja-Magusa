using System.Text;

namespace Launcher.Core.Tests;

public sealed class PatchManifestTests
{
    private static byte[] WriteManifest(params PatchManifestEntry[] entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            PatchManifest.Write(writer, entries);

        return stream.ToArray();
    }

    private static PatchManifestEntry CreateEntry(string fileName = "Zircon.exe", long compressedLength = 128)
    {
        return new PatchManifestEntry
        {
            FileName = fileName,
            CompressedLength = compressedLength,
            CheckSum = Enumerable.Repeat((byte)0x5a, 16).ToArray(),
        };
    }

    [Fact]
    public void Round_trip_preserves_entries_and_binary_layout()
    {
        PatchManifestEntry[] entries =
        {
            CreateEntry("Zircon.exe", 100),
            CreateEntry("Data\\System.db", 200),
        };

        byte[] bytes = WriteManifest(entries);

        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        // The first entry must match the original wire format exactly so the
        // paired PatchManager manifest stays readable by older launchers.
        Assert.Equal("Zircon.exe", reader.ReadString());
        Assert.Equal(100L, reader.ReadInt64());
        Assert.Equal(16, reader.ReadInt32());
        Assert.Equal(Enumerable.Repeat((byte)0x5a, 16).ToArray(), reader.ReadBytes(16));

        var remaining = PatchManifest.Read(reader);
        Assert.Single(remaining);
        Assert.Equal("Data\\System.db", remaining[0].FileName);
    }

    [Fact]
    public void Empty_manifest_reads_as_empty_list()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        Assert.Empty(PatchManifest.Read(reader));
    }

    [Fact]
    public void Manifest_reads_from_non_seekable_network_stream()
    {
        byte[] bytes = WriteManifest(CreateEntry("Zircon.exe"));
        using var stream = new NonSeekableReadStream(bytes);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        var entries = PatchManifest.Read(reader);

        Assert.Single(entries);
        Assert.Equal("Zircon.exe", entries[0].FileName);
    }

    private sealed class NonSeekableReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override int ReadByte() => _inner.ReadByte();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Duplicate_payload_names_are_rejected()
    {
        var entries = new[] { CreateEntry("Data\\System.db"), CreateEntry("Data-System.db") };
        Assert.Throws<ArgumentException>(() => WriteManifest(entries));
    }

    [Fact]
    public void Duplicate_file_names_in_a_received_manifest_are_rejected()
    {
        byte[] first = WriteManifest(CreateEntry("Zircon.exe"));
        using var stream = new MemoryStream();
        stream.Write(first);
        stream.Write(first);
        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        Assert.Throws<InvalidDataException>(() => PatchManifest.Read(reader));
    }

    [Fact]
    public void Entry_with_parent_traversal_is_rejected()
    {
        // Write the malicious entry bypassing PatchManifest.Write validation so
        // the reader is exercised against hostile on-disk bytes.
        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("..\\evil.exe");
                writer.Write(128L);
                writer.Write(16);
                writer.Write(Enumerable.Repeat((byte)0x5a, 16).ToArray());
            }
            bytes = stream.ToArray();
        }

        using var readStream = new MemoryStream(bytes);
        using var reader = new BinaryReader(readStream, Encoding.UTF8);

        var exception = Assert.Throws<InvalidDataException>(() => PatchManifest.Read(reader));
        Assert.Contains("..\\evil.exe", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_with_empty_check_sum_is_rejected()
    {
        PatchManifestEntry entry = CreateEntry();
        entry.CheckSum = Array.Empty<byte>();

        var exception = Assert.Throws<ArgumentException>(() => WriteManifest(entry));
        Assert.Contains("check sum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Truncated_manifest_is_rejected_instead_of_partially_read()
    {
        byte[] bytes = WriteManifest(CreateEntry());
        byte[] truncated = bytes[..(bytes.Length - 4)];

        using var stream = new MemoryStream(truncated);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        Assert.Throws<InvalidDataException>(() => PatchManifest.Read(reader));
    }

    [Fact]
    public void IsMatch_compares_prefix_at_offset_like_the_original_launcher()
    {
        byte[] full = { 1, 2, 3, 4, 5 };
        byte[] prefix = { 3, 4 };

        Assert.True(PatchManifest.IsMatch(full, full));
        Assert.True(PatchManifest.IsMatch(full, prefix, 2));
        Assert.False(PatchManifest.IsMatch(full, prefix, 3));
        Assert.False(PatchManifest.IsMatch(null, prefix));
        Assert.False(PatchManifest.IsMatch(full, null));
        // This documents why manifest entries with empty check sums are
        // rejected: an empty check sum matches everything at any offset.
        Assert.True(PatchManifest.IsMatch(full, Array.Empty<byte>(), 5));
    }
}
