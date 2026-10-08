using System.Diagnostics;
using System.Runtime.Versioning;
using Mir3.Web.Services;

namespace Mir3.Web.Tests;

[SupportedOSPlatform("linux")]
public sealed class VerificationTransportLockManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mir3-verification-lock-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Lock_is_exclusive_across_instances_and_reusable_after_release()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (connectionString, lockDirectory) = CreatePaths();
        using var firstManager = new VerificationTransportLockManager(connectionString, lockDirectory);
        using var secondManager = new VerificationTransportLockManager(connectionString, lockDirectory);
        var registrationId = Guid.NewGuid();

        await using var first = Assert.IsType<VerificationTransportLock>(firstManager.TryAcquire(registrationId));
        Assert.Null(secondManager.TryAcquire(registrationId));
        await first.DisposeAsync();
        await using var second = Assert.IsType<VerificationTransportLock>(secondManager.TryAcquire(registrationId));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(lockDirectory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(lockDirectory, firstManager.GetLockFileName(registrationId))));
    }

    [Fact]
    public async Task Lock_is_exclusive_across_processes_and_reusable_after_holder_exits()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (connectionString, lockDirectory) = CreatePaths();
        using var manager = new VerificationTransportLockManager(connectionString, lockDirectory);
        var registrationId = Guid.NewGuid();
        var probePath = Path.Combine(
            GetRepositoryRoot(),
            "Mir3.Web.FdZeroProbe",
            "bin",
            GetBuildConfiguration(),
            "net10.0",
            "Mir3.Web.FdZeroProbe");
        Assert.True(File.Exists(probePath), $"verification lock probe was not built at {probePath}");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = probePath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add("verification-lock-hold");
        process.StartInfo.ArgumentList.Add(connectionString);
        process.StartInfo.ArgumentList.Add(lockDirectory);
        process.StartInfo.ArgumentList.Add(registrationId.ToString("D"));
        Assert.True(process.Start());
        // Cold-starting the probe process (runtime init + JIT + SQLite open) can exceed
        // several seconds on a heavily loaded CI host; keep the handshake bounded but generous.
        var handshakeTimeout = TimeSpan.FromSeconds(30);
        Assert.Equal("locked", await process.StandardOutput.ReadLineAsync().WaitAsync(handshakeTimeout));

        Assert.Null(manager.TryAcquire(registrationId));
        await process.StandardInput.WriteLineAsync("release");
        await process.WaitForExitAsync().WaitAsync(handshakeTimeout);
        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await process.StandardError.ReadToEndAsync());
        await using var acquired = Assert.IsType<VerificationTransportLock>(manager.TryAcquire(registrationId));
    }

    [Fact]
    public void Constructor_rejects_memory_and_relative_database_modes()
    {
        if (!OperatingSystem.IsLinux()) return;
        Directory.CreateDirectory(_root);
        var lockDirectory = Path.Combine(_root, "locks");

        Assert.Throws<InvalidOperationException>(() =>
            new VerificationTransportLockManager("Data Source=:memory:", lockDirectory));
        Assert.Throws<InvalidOperationException>(() =>
            new VerificationTransportLockManager("Data Source=portal.db", lockDirectory));
        Assert.Throws<InvalidOperationException>(() =>
            new VerificationTransportLockManager("Data Source=ignored;Mode=Memory", lockDirectory));
    }

    [Fact]
    public void Constructor_rejects_symlinked_database_or_lock_directory_paths()
    {
        if (!OperatingSystem.IsLinux()) return;
        Directory.CreateDirectory(_root);
        var realDatabase = Path.Combine(_root, "portal-real.db");
        File.WriteAllBytes(realDatabase, []);
        var databaseLink = Path.Combine(_root, "portal.db");
        File.CreateSymbolicLink(databaseLink, realDatabase);
        var realLocks = Path.Combine(_root, "locks-real");
        Directory.CreateDirectory(realLocks);
        File.SetUnixFileMode(realLocks, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var lockLink = Path.Combine(_root, "locks");
        Directory.CreateSymbolicLink(lockLink, realLocks);

        Assert.Throws<InvalidOperationException>(() =>
            new VerificationTransportLockManager($"Data Source={databaseLink}", Path.Combine(_root, "safe-locks")));
        Assert.Throws<InvalidOperationException>(() =>
            new VerificationTransportLockManager($"Data Source={realDatabase}", lockLink));
    }

    [Fact]
    public void Constructor_and_acquire_reject_unsafe_permissions_symlinks_and_non_regular_lock_files()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (connectionString, lockDirectory) = CreatePaths(createLockDirectory: true);
        File.SetUnixFileMode(lockDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        Assert.Throws<InvalidOperationException>(() =>
            new VerificationTransportLockManager(connectionString, lockDirectory));

        File.SetUnixFileMode(lockDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var manager = new VerificationTransportLockManager(connectionString, lockDirectory);
        var registrationId = Guid.NewGuid();
        var lockPath = Path.Combine(lockDirectory, manager.GetLockFileName(registrationId));
        var target = Path.Combine(_root, "target");
        File.WriteAllBytes(target, []);
        File.CreateSymbolicLink(lockPath, target);
        Assert.Throws<InvalidOperationException>(() => manager.TryAcquire(registrationId));
        File.Delete(lockPath);

        Directory.CreateDirectory(lockPath);
        Assert.Throws<InvalidOperationException>(() => manager.TryAcquire(registrationId));
        Directory.Delete(lockPath);

        File.WriteAllBytes(lockPath, []);
        File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.Throws<InvalidOperationException>(() => manager.TryAcquire(registrationId));
    }

    [Fact]
    public void Constructor_rejects_an_existing_symlinked_lock_file()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (connectionString, lockDirectory) = CreatePaths();
        var registrationId = Guid.NewGuid();
        string lockFileName;
        using (var manager = new VerificationTransportLockManager(connectionString, lockDirectory))
            lockFileName = manager.GetLockFileName(registrationId);
        var target = Path.Combine(_root, "existing-target");
        File.WriteAllBytes(target, []);
        File.CreateSymbolicLink(Path.Combine(lockDirectory, lockFileName), target);

        Assert.Throws<InvalidOperationException>(() =>
            new VerificationTransportLockManager(connectionString, lockDirectory));
    }

    private (string ConnectionString, string LockDirectory) CreatePaths(bool createLockDirectory = false)
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "portal.db");
        File.WriteAllBytes(databasePath, []);
        var lockDirectory = Path.Combine(_root, "locks");
        if (createLockDirectory)
        {
            Directory.CreateDirectory(lockDirectory);
            File.SetUnixFileMode(lockDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return ($"Data Source={databasePath}", lockDirectory);
    }

    private static string GetRepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string GetBuildConfiguration() =>
        AppContext.BaseDirectory.Contains("/Release/", StringComparison.Ordinal) ? "Release" : "Debug";

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
