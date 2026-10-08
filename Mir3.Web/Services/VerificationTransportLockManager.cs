using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Mir3.Web.Services;

public sealed class VerificationTransportLockManager : IDisposable
{
    private const int O_RDONLY = 0;
    private const int O_RDWR = 2;
    private const int O_CREAT = 0x40;
    private const int O_NONBLOCK = 0x800;
    private const int O_CLOEXEC = 0x80000;
    private const int O_NOFOLLOW = 0x20000;
    private const int O_DIRECTORY = 0x10000;
    private const int AT_EMPTY_PATH = 0x1000;
    private const int AT_SYMLINK_NOFOLLOW = 0x100;
    private const uint STATX_TYPE = 0x0001;
    private const uint STATX_MODE = 0x0002;
    private const uint STATX_UID = 0x0008;
    private const ushort S_IFMT = 0xF000;
    private const ushort S_IFREG = 0x8000;
    private const ushort S_IFDIR = 0x4000;
    private const ushort PermissionMask = 0x01FF;
    private const ushort OwnerDirectoryMode = 0x01C0; // 0700
    private const ushort OwnerFileMode = 0x0180; // 0600
    private const int LOCK_EX = 2;
    private const int LOCK_NB = 4;
    private const int EAGAIN = 11;
    private const int EWOULDBLOCK = 11;
    private const int ENOENT = 2;

    private readonly string _databaseIdentity;
    private readonly SafeUnixFileDescriptor _directory;
    private bool _disposed;

    internal VerificationTransportLockManager(string connectionString, string lockDirectory)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Verification e-mail transport locking is supported only on Linux.");

        var databasePath = ValidateDatabaseConnection(connectionString);
        ValidateDatabasePath(databasePath);
        _databaseIdentity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(databasePath)));
        var directory = OpenAbsoluteDirectory(lockDirectory, createFinal: true);
        try
        {
            ValidateDescriptor(directory, S_IFDIR, OwnerDirectoryMode, "verification transport lock directory");
            ValidateExistingLockFiles(directory, Path.GetFullPath(lockDirectory));
            _directory = directory;
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    internal string GetLockFileName(Guid registrationId)
    {
        if (registrationId == Guid.Empty)
            throw new ArgumentException("The registration ID is required.", nameof(registrationId));
        return $"{_databaseIdentity}-{registrationId:N}.lock";
    }

    internal VerificationTransportLock? TryAcquire(Guid registrationId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fileName = GetLockFileName(registrationId);
        var descriptor = openat(
            _directory,
            fileName,
            O_RDWR | O_CREAT | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW,
            OwnerFileMode);
        if (descriptor < 0)
            throw CreateInvalidOperation("Could not safely open the verification transport lock file.");

        var handle = new SafeUnixFileDescriptor(descriptor);
        try
        {
            ValidateDescriptor(handle, S_IFREG, OwnerFileMode, "verification transport lock file");
            if (flock(handle, LOCK_EX | LOCK_NB) == 0)
                return new VerificationTransportLock(handle);

            var error = Marshal.GetLastPInvokeError();
            if (error is EAGAIN or EWOULDBLOCK)
            {
                handle.Dispose();
                return null;
            }

            throw new InvalidOperationException(
                "Could not acquire the verification transport lock.",
                new Win32Exception(error));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _directory.Dispose();
    }

    private static string ValidateDatabaseConnection(string connectionString)
    {
        SqliteConnectionStringBuilder builder;
        try
        {
            builder = new SqliteConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("The portal SQLite connection string is invalid.", exception);
        }

        if (builder.Mode == SqliteOpenMode.Memory ||
            string.IsNullOrWhiteSpace(builder.DataSource) ||
            string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(builder.DataSource))
        {
            throw new InvalidOperationException(
                "Verification e-mail delivery requires an absolute file-backed SQLite database path.");
        }

        return Path.GetFullPath(builder.DataSource);
    }

    private static void ValidateDatabasePath(string databasePath)
    {
        var parent = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("The portal SQLite database path has no parent directory.");
        using var parentHandle = OpenAbsoluteDirectory(parent, createFinal: true);
        var descriptor = openat(
            parentHandle,
            Path.GetFileName(databasePath),
            O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW);
        if (descriptor < 0)
        {
            if (Marshal.GetLastPInvokeError() == ENOENT) return;
            throw CreateInvalidOperation("The portal SQLite database path is unsafe.");
        }

        using var databaseHandle = new SafeUnixFileDescriptor(descriptor);
        var metadata = ReadMetadata(databaseHandle, "portal SQLite database file");
        if ((metadata.Mode & S_IFMT) != S_IFREG ||
            metadata.Uid != geteuid() ||
            (metadata.Mode & 0x0012) != 0)
        {
            throw new InvalidOperationException(
                "The portal SQLite database file must be regular, owned by the effective user, and not group/other writable.");
        }
    }

    private static void ValidateExistingLockFiles(
        SafeUnixFileDescriptor directory,
        string lockDirectory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(lockDirectory))
        {
            var fileName = Path.GetFileName(path);
            if (!IsLockFileName(fileName))
                throw new InvalidOperationException("The verification transport lock directory contains an unexpected entry.");

            var descriptor = openat(
                directory,
                fileName,
                O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW);
            if (descriptor < 0)
                throw CreateInvalidOperation("An existing verification transport lock file is unsafe.");

            using var handle = new SafeUnixFileDescriptor(descriptor);
            ValidateDescriptor(handle, S_IFREG, OwnerFileMode, "verification transport lock file");
        }
    }

    private static bool IsLockFileName(string fileName)
    {
        if (fileName.Length != 64 + 1 + 32 + 5 || fileName[64] != '-' ||
            !fileName.EndsWith(".lock", StringComparison.Ordinal))
            return false;

        foreach (var character in fileName.AsSpan(0, 64))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        }

        foreach (var character in fileName.AsSpan(65, 32))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        }

        return true;
    }

    private static SafeUnixFileDescriptor OpenAbsoluteDirectory(string path, bool createFinal)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("The verification transport lock directory must be an absolute path.");

        var fullPath = Path.GetFullPath(path);
        var components = fullPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var descriptor = open("/", O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW | O_DIRECTORY);
        if (descriptor < 0) throw CreateInvalidOperation("Could not open the filesystem root safely.");

        var current = new SafeUnixFileDescriptor(descriptor);
        try
        {
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                var nextDescriptor = openat(
                    current,
                    component,
                    O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW | O_DIRECTORY);
                if (nextDescriptor < 0 &&
                    createFinal &&
                    index == components.Length - 1 &&
                    Marshal.GetLastPInvokeError() == ENOENT)
                {
                    if (mkdirat(current, component, OwnerDirectoryMode) != 0)
                        throw CreateInvalidOperation("Could not create the verification transport lock directory safely.");
                    nextDescriptor = openat(
                        current,
                        component,
                        O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW | O_DIRECTORY);
                }

                if (nextDescriptor < 0)
                    throw CreateInvalidOperation("A verification transport filesystem path component is unsafe or missing.");

                var next = new SafeUnixFileDescriptor(nextDescriptor);
                current.Dispose();
                current = next;
            }

            var result = current;
            current = null!;
            return result;
        }
        finally
        {
            current?.Dispose();
        }
    }

    private static void ValidateDescriptor(
        SafeUnixFileDescriptor descriptor,
        ushort expectedType,
        ushort expectedPermissions,
        string description)
    {
        var metadata = ReadMetadata(descriptor, description);
        if ((metadata.Mode & S_IFMT) != expectedType ||
            (metadata.Mode & PermissionMask) != expectedPermissions ||
            metadata.Uid != geteuid())
        {
            throw new InvalidOperationException(
                $"The {description} must be owned by the effective user with mode {Convert.ToString(expectedPermissions, 8)}.");
        }
    }


    private static LinuxStatx ReadMetadata(SafeUnixFileDescriptor descriptor, string description)
    {
        if (statx(
                descriptor,
                string.Empty,
                AT_EMPTY_PATH | AT_SYMLINK_NOFOLLOW,
                STATX_TYPE | STATX_MODE | STATX_UID,
                out var metadata) != 0)
        {
            throw CreateInvalidOperation($"Could not inspect the {description} safely.");
        }

        return metadata;
    }

    private static InvalidOperationException CreateInvalidOperation(string message) =>
        new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int openat(SafeUnixFileDescriptor directoryDescriptor, string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int openat(
        SafeUnixFileDescriptor directoryDescriptor,
        string path,
        int flags,
        ushort mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int mkdirat(SafeUnixFileDescriptor directoryDescriptor, string path, ushort mode);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int statx(
        SafeUnixFileDescriptor directoryDescriptor,
        string path,
        int flags,
        uint mask,
        out LinuxStatx metadata);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint geteuid();

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int flock(SafeUnixFileDescriptor descriptor, int operation);
}

internal sealed class VerificationTransportLock(SafeUnixFileDescriptor descriptor) : IAsyncDisposable, IDisposable
{
    private SafeUnixFileDescriptor? _descriptor = descriptor;

    public void Dispose() => Interlocked.Exchange(ref _descriptor, null)?.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
