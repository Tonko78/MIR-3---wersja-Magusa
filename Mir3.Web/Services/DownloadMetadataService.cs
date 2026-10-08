using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using Mir3.Web.Options;

namespace Mir3.Web.Services;

public sealed record DownloadMetadata(
    bool IsAvailable,
    string FileName,
    string Version,
    long ByteCount,
    string Sha256)
{
    public static DownloadMetadata Unavailable(string fileName, string version) =>
        new(false, fileName, version, 0, string.Empty);
}

public sealed class DownloadSnapshot : IDisposable
{
    private FileStream? _stream;

    internal DownloadSnapshot(DownloadMetadata metadata, FileStream stream)
    {
        Metadata = metadata;
        _stream = stream;
    }

    public DownloadMetadata Metadata { get; }

    public FileStream Stream => _stream ?? throw new ObjectDisposedException(nameof(DownloadSnapshot));

    internal FileStream TakeStream()
    {
        return Interlocked.Exchange(ref _stream, null)
            ?? throw new ObjectDisposedException(nameof(DownloadSnapshot));
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
    }
}

public sealed class DownloadMetadataService(
    IOptions<DownloadOptions> options,
    ILogger<DownloadMetadataService> logger)
{
    private const int O_RDONLY = 0;
    private const int O_NONBLOCK = 0x800;
    private const int O_CLOEXEC = 0x80000;
    private const int O_NOFOLLOW = 0x20000;
    private const int O_DIRECTORY = 0x10000;
    private const int AT_EMPTY_PATH = 0x1000;
    private const int AT_SYMLINK_NOFOLLOW = 0x100;
    private const uint STATX_BASIC_STATS = 0x07ff;
    private const ushort S_IFMT = 0xF000;
    private const ushort S_IFREG = 0x8000;
    private const uint FILE_TYPE_DISK = 0x0001;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    private readonly DownloadOptions _options = options.Value;
    private readonly ILogger<DownloadMetadataService> _logger = logger;
    private readonly object _cacheLock = new();
    private CachedHash? _cachedHash;

    public DownloadMetadata GetMetadata()
    {
        FileStream? stream = null;
        try
        {
            stream = OpenVerifiedArchive();
            var fingerprint = GetFingerprint(stream);
            string hash;
            lock (_cacheLock)
            {
                if (_cachedHash is { } cached && cached.Fingerprint == fingerprint)
                {
                    hash = cached.Value;
                }
                else
                {
                    stream.Position = 0;
                    hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                    if (GetFingerprint(stream) != fingerprint)
                    {
                        throw new IOException("The client archive changed while it was being read.");
                    }

                    _cachedHash = new CachedHash(fingerprint, hash);
                }
            }

            return new DownloadMetadata(true, _options.FileName, _options.Version, fingerprint.ByteCount, hash);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The configured client archive could not be read.");
            return DownloadMetadata.Unavailable(_options.FileName, _options.Version);
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public DownloadSnapshot OpenDownload(bool computeHash = true)
    {
        FileStream? stream = null;
        try
        {
            stream = OpenVerifiedArchive();
            var fingerprint = GetFingerprint(stream);
            var hash = string.Empty;
            if (computeHash)
            {
                stream.Position = 0;
                hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }

            // A file replaced by an atomic publication remains available through
            // this handle. Reject an in-place change so the metadata describes the
            // same bounded stream that will be handed to ASP.NET.
            if (GetFingerprint(stream) != fingerprint)
            {
                throw new IOException("The client archive changed while it was being read.");
            }

            stream.Position = 0;
            return new DownloadSnapshot(
                new DownloadMetadata(true, _options.FileName, _options.Version, fingerprint.ByteCount, hash),
                stream);
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    public FileStream OpenRead()
    {
        using var snapshot = OpenDownload();
        return snapshot.TakeStream();
    }

    public string GetArchivePath() => Path.Combine(_options.Directory, _options.FileName);

    private FileStream OpenVerifiedArchive()
    {
        return OperatingSystem.IsLinux()
            ? OpenVerifiedLinuxArchive()
            : OpenVerifiedPortableArchive();
    }

    private FileStream OpenVerifiedLinuxArchive()
    {
        var directoryDescriptor = open(
            _options.Directory,
            O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW | O_DIRECTORY);
        if (directoryDescriptor < 0)
        {
            throw OpenFailure(_options.Directory);
        }

        using var directoryHandle = new SafeFileHandle((IntPtr)directoryDescriptor, ownsHandle: true);
        var fileDescriptor = openat(
            directoryHandle,
            _options.FileName,
            O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW);
        if (fileDescriptor < 0)
        {
            throw OpenFailure(GetArchivePath());
        }

        var fileHandle = new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
        if (statx(
                fileHandle,
                string.Empty,
                AT_EMPTY_PATH | AT_SYMLINK_NOFOLLOW,
                STATX_BASIC_STATS,
                out var metadata) != 0 ||
            (metadata.Mode & S_IFMT) != S_IFREG)
        {
            fileHandle.Dispose();
            throw new IOException("The configured client archive is not a regular file.");
        }

        try
        {
            return new FileStream(fileHandle, FileAccess.Read, 64 * 1024, isAsync: false);
        }
        catch
        {
            fileHandle.Dispose();
            throw;
        }
    }

    private FileStream OpenVerifiedPortableArchive()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new IOException("Regular-file verification is unavailable on this platform.");
        }

        var path = GetArchivePath();
        EnsurePortableRegularFile(path);

        FileStream? stream = null;
        try
        {
            var handle = CreateFile(
                path,
                GENERIC_READ,
                FILE_SHARE_READ,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_OVERLAPPED | FILE_FLAG_SEQUENTIAL_SCAN,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new IOException($"The configured client archive could not be opened: {path} (error {error}).");
            }

            stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: true);
            EnsureWindowsRegularFile(stream);
            return stream;
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    private static void EnsurePortableRegularFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException("The configured client archive is not a regular file.");
        }
    }

    private static FileFingerprint GetFingerprint(FileStream stream)
    {
        if (OperatingSystem.IsLinux())
        {
            if (statx(
                    stream.SafeFileHandle,
                    string.Empty,
                    AT_EMPTY_PATH | AT_SYMLINK_NOFOLLOW,
                    STATX_BASIC_STATS,
                    out var metadata) != 0 ||
                (metadata.Mode & S_IFMT) != S_IFREG ||
                (metadata.Mask & STATX_BASIC_STATS) != STATX_BASIC_STATS ||
                metadata.Size > long.MaxValue)
            {
                throw new IOException("The configured client archive could not be fingerprinted.");
            }

            return new FileFingerprint(
                (long)metadata.Size,
                metadata.MtimeSeconds,
                metadata.MtimeNanoseconds,
                metadata.Inode,
                ((ulong)metadata.DeviceMajor << 32) | metadata.DeviceMinor);
        }

        if (OperatingSystem.IsWindows())
        {
            return GetWindowsFingerprint(stream);
        }

        throw new IOException("Regular-file fingerprinting is unavailable on this platform.");
    }

    private static FileFingerprint GetWindowsFingerprint(FileStream stream)
    {
        EnsureWindowsRegularFile(stream);
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information))
        {
            throw new IOException("The configured client archive could not be fingerprinted.");
        }

        var byteCount = ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
        if (byteCount > long.MaxValue)
        {
            throw new IOException("The configured client archive is too large.");
        }

        var lastWriteTime = ((long)information.LastWriteTimeHigh << 32) | information.LastWriteTimeLow;
        var fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return new FileFingerprint(
            (long)byteCount,
            lastWriteTime,
            0,
            fileId,
            information.VolumeSerialNumber);
    }

    private static void EnsureWindowsRegularFile(FileStream stream)
    {
        if (GetFileType(stream.SafeFileHandle) != FILE_TYPE_DISK ||
            !GetFileInformationByHandle(stream.SafeFileHandle, out var information) ||
            (information.FileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) != 0)
        {
            throw new IOException("The configured client archive is not a regular file.");
        }
    }

    private static IOException OpenFailure(string path)
    {
        var error = Marshal.GetLastPInvokeError();
        return new IOException($"The configured client archive could not be opened: {path} (errno {error}).");
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)]
        public uint Mask;

        [FieldOffset(28)]
        public ushort Mode;

        [FieldOffset(32)]
        public ulong Inode;

        [FieldOffset(40)]
        public ulong Size;

        [FieldOffset(112)]
        public long MtimeSeconds;

        [FieldOffset(120)]
        public uint MtimeNanoseconds;

        // Linux statx stores stx_rdev_major/minor at 128/132 and the
        // containing filesystem's stx_dev_major/minor at 136/140.
        [FieldOffset(136)]
        public uint DeviceMajor;

        [FieldOffset(140)]
        public uint DeviceMinor;
    }

    private readonly record struct FileFingerprint(
        long ByteCount,
        long LastWriteTime,
        uint LastWriteTimeNanoseconds,
        ulong FileId,
        ulong VolumeId);

    private sealed record CachedHash(FileFingerprint Fingerprint, string Value);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int openat(SafeFileHandle directoryDescriptor, string path, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int statx(
        SafeFileHandle directoryDescriptor,
        string path,
        int flags,
        uint mask,
        out LinuxStatx metadata);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle fileHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation information);
}
