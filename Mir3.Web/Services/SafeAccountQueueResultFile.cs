using System.Runtime.InteropServices;

namespace Mir3.Web.Services;

internal static class SafeAccountQueueResultFile
{
    internal const int MaximumResultBytes = 64 * 1024;

    private const int O_RDONLY = 0;
    private const int O_NONBLOCK = 0x800;
    private const int O_CLOEXEC = 0x80000;
    private const int O_NOFOLLOW = 0x20000;
    private const int O_DIRECTORY = 0x10000;
    private const int AT_EMPTY_PATH = 0x1000;
    private const int AT_SYMLINK_NOFOLLOW = 0x100;
    private const uint STATX_TYPE = 0x0001;
    private const uint STATX_SIZE = 0x0200;
    private const ushort S_IFMT = 0xF000;
    private const ushort S_IFREG = 0x8000;
    private const int ENOENT = 2;
    private const int EINTR = 4;

    internal static ResultSnapshotRead Read(
        string rootPath,
        string fileName,
        AccountQueueClientOperations operations,
        string directoryName = "results")
    {
        if (!operations.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "The account queue integration is supported only on Linux.");
        }

        return ReadLinux(rootPath, directoryName, fileName, operations);
    }

    private static ResultSnapshotRead ReadLinux(
        string rootPath,
        string directoryName,
        string fileName,
        AccountQueueClientOperations operations)
    {
        using var rootHandle = OpenDirectory(rootPath);
        if (rootHandle is null) return ResultSnapshotRead.Invalid;

        using var resultsHandle = OpenDirectoryAt(rootHandle, directoryName);
        if (resultsHandle is null) return ResultSnapshotRead.Invalid;

        operations.BeforeResultOpen?.Invoke();

        var descriptor = openat(
            resultsHandle,
            fileName,
            O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW);
        if (descriptor < 0)
        {
            return Marshal.GetLastPInvokeError() == ENOENT
                ? ResultSnapshotRead.Pending
                : ResultSnapshotRead.Invalid;
        }

        using var fileHandle = new SafeUnixFileDescriptor(descriptor);
        operations.AfterResultOpenDescriptor?.Invoke(descriptor);
        if (statx(
                fileHandle,
                string.Empty,
                AT_EMPTY_PATH | AT_SYMLINK_NOFOLLOW,
                STATX_TYPE | STATX_SIZE,
                out var metadata) != 0 ||
            (metadata.Mode & S_IFMT) != S_IFREG ||
            metadata.Size == 0 ||
            metadata.Size > MaximumResultBytes)
        {
            return ResultSnapshotRead.Invalid;
        }

        operations.AfterResultInitialLengthObserved?.Invoke();
        return ReadBoundedSnapshot(fileHandle);
    }

    private static ResultSnapshotRead ReadBoundedSnapshot(SafeUnixFileDescriptor handle)
    {
        var buffer = new byte[MaximumResultBytes + 1];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var chunk = new byte[Math.Min(8192, buffer.Length - totalRead)];
            var read = pread(handle, chunk, (nuint)chunk.Length, totalRead);
            if (read == 0) break;
            if (read < 0)
            {
                if (Marshal.GetLastPInvokeError() == EINTR) continue;
                return ResultSnapshotRead.Invalid;
            }

            var readCount = checked((int)read);
            Buffer.BlockCopy(chunk, 0, buffer, totalRead, readCount);
            totalRead += readCount;
        }

        if (totalRead == 0 || totalRead > MaximumResultBytes)
        {
            return ResultSnapshotRead.Invalid;
        }

        return ResultSnapshotRead.Ready(buffer.AsSpan(0, totalRead).ToArray());
    }

    private static SafeUnixFileDescriptor? OpenDirectory(string path)
    {
        var descriptor = open(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW | O_DIRECTORY);
        return descriptor >= 0 ? new SafeUnixFileDescriptor(descriptor) : null;
    }

    private static SafeUnixFileDescriptor? OpenDirectoryAt(SafeUnixFileDescriptor parent, string name)
    {
        var descriptor = openat(
            parent,
            name,
            O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW | O_DIRECTORY);
        return descriptor >= 0 ? new SafeUnixFileDescriptor(descriptor) : null;
    }

    // Linux statx is a fixed, architecture-independent UAPI layout. Only the
    // stable mode and size fields are projected; libc's architecture-varying
    // struct stat is deliberately not marshalled here.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(28)]
        public ushort Mode;

        [FieldOffset(40)]
        public ulong Size;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int openat(SafeUnixFileDescriptor directoryDescriptor, string path, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int statx(
        SafeUnixFileDescriptor directoryDescriptor,
        string path,
        int flags,
        uint mask,
        out LinuxStatx metadata);

    [DllImport("libc", EntryPoint = "pread", SetLastError = true)]
    private static extern nint pread(
        SafeUnixFileDescriptor descriptor,
        [Out] byte[] buffer,
        nuint count,
        long offset);
}

internal enum ResultSnapshotReadStatus
{
    Pending,
    Ready,
    Invalid
}

internal sealed record ResultSnapshotRead(ResultSnapshotReadStatus Status, byte[]? Bytes)
{
    internal static ResultSnapshotRead Pending { get; } = new(ResultSnapshotReadStatus.Pending, null);
    internal static ResultSnapshotRead Invalid { get; } = new(ResultSnapshotReadStatus.Invalid, null);
    internal static ResultSnapshotRead Ready(byte[] bytes) => new(ResultSnapshotReadStatus.Ready, bytes);
}
