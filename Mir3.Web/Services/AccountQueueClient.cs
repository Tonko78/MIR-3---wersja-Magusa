using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AccountPortal.Contracts;
using Microsoft.Extensions.Options;
using Mir3.Web.Options;

namespace Mir3.Web.Services;

public sealed class AccountQueueClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly QueueOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly AccountQueueClientOperations _operations;
    private readonly string _rootPath;
    private readonly string _incomingPath;
    private readonly string _resultsPath;

    public AccountQueueClient(IOptions<QueueOptions> options, TimeProvider? timeProvider = null)
        : this(options, timeProvider, AccountQueueClientOperations.Default)
    {
    }

    internal AccountQueueClient(
        IOptions<QueueOptions> options,
        TimeProvider? timeProvider,
        AccountQueueClientOperations operations)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(operations);
        if (!operations.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "The account queue integration is supported only on Linux.");
        }

        _options = options.Value;
        var validation = new QueueOptionsValidator(operations.IsLinux)
            .Validate(Microsoft.Extensions.Options.Options.DefaultName, _options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                Microsoft.Extensions.Options.Options.DefaultName,
                typeof(QueueOptions),
                validation.Failures);
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _operations = operations;
        _rootPath = Path.GetFullPath(_options.RootPath);
        EnsureRealDirectory(_rootPath, create: true);
        _incomingPath = Path.Combine(_rootPath, "incoming");
        _resultsPath = Path.Combine(_rootPath, "results");
        EnsureRealDirectory(_incomingPath, create: true);
        EnsureRealDirectory(_resultsPath, create: true);
    }

    public Task<AccountQueuePublishResult> WriteCommandAsync(
        AccountCommandType type,
        string email,
        byte[]? passwordHash,
        CancellationToken cancellationToken = default) =>
        WriteCommandAsync(Guid.NewGuid(), type, email, passwordHash, cancellationToken);

    public async Task<AccountQueuePublishResult> WriteCommandAsync(
        Guid requestId,
        AccountCommandType type,
        string email,
        byte[]? passwordHash,
        CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("The queue request ID is required.", nameof(requestId));
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        if (type is (AccountCommandType.CreateAccount or AccountCommandType.ResetPassword) && passwordHash?.Length != 36)
        {
            throw new ArgumentException($"{type} requires a 36-byte password hash.", nameof(passwordHash));
        }

        if (type is not (AccountCommandType.CreateAccount or AccountCommandType.ResetPassword) && passwordHash is not null)
        {
            throw new ArgumentException("Only CreateAccount and ResetPassword can include a password hash.", nameof(passwordHash));
        }

        EnsureRealDirectory(_rootPath, create: false);
        EnsureRealDirectory(_incomingPath, create: false);
        var command = AccountCommand.Create(
            requestId,
            type,
            email,
            passwordHash is null ? null : Convert.ToBase64String(passwordHash),
            _timeProvider.GetUtcNow(),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        AccountCommandSigner.Sign(command, _options.HmacKeyBase64!);
        cancellationToken.ThrowIfCancellationRequested();

        var finalPath = Path.Combine(_incomingPath, $"{requestId:D}.json");
        var existing = ReadExistingCommand(requestId, type, command.Email, command.PasswordHashBase64);
        if (existing is not null)
        {
            return new AccountQueuePublishResult(
                requestId,
                AccountQueuePublishOutcome.Published,
                existing);
        }

        var temporaryPath = $"{finalPath}.{Guid.NewGuid():N}.tmp";
        var temporaryCreated = false;
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                temporaryCreated = true;
                await JsonSerializer.SerializeAsync(stream, command, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _operations.BeforeCommandRename?.Invoke();
            try
            {
                File.Move(temporaryPath, finalPath);
                temporaryCreated = false;
            }
            catch (IOException)
            {
                var published = ReadExistingCommand(requestId, type, command.Email, command.PasswordHashBase64);
                if (published is null) throw;
                DeleteBestEffort(temporaryPath);
                temporaryCreated = false;
                return new AccountQueuePublishResult(
                    requestId,
                    AccountQueuePublishOutcome.Published,
                    published);
            }

            try
            {
                _operations.SyncDirectory(_incomingPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return new AccountQueuePublishResult(
                    requestId,
                    AccountQueuePublishOutcome.Uncertain,
                    command);
            }

            return new AccountQueuePublishResult(
                requestId,
                AccountQueuePublishOutcome.Published,
                command);
        }
        catch
        {
            if (temporaryCreated) DeleteBestEffort(temporaryPath);
            throw;
        }
    }

    private AccountCommand? ReadExistingCommand(
        Guid requestId,
        AccountCommandType expectedType,
        string expectedEmail,
        string? expectedPasswordHashBase64)
    {
        var snapshot = SafeAccountQueueResultFile.Read(
            _rootPath,
            $"{requestId:D}.json",
            _operations,
            "incoming");
        if (snapshot.Status == ResultSnapshotReadStatus.Pending)
        {
            return null;
        }

        if (snapshot.Status != ResultSnapshotReadStatus.Ready || snapshot.Bytes is null)
        {
            throw new InvalidDataException("The existing account queue command is invalid.");
        }

        AccountCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<AccountCommand>(snapshot.Bytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The existing account queue command is invalid.", exception);
        }

        if (command is null ||
            command.Id != requestId ||
            command.Type != expectedType ||
            !string.Equals(command.Email, expectedEmail, StringComparison.Ordinal) ||
            !string.Equals(command.PasswordHashBase64, expectedPasswordHashBase64, StringComparison.Ordinal) ||
            !AccountCommandSigner.Verify(command, _options.HmacKeyBase64))
        {
            throw new InvalidDataException("The existing account queue command does not match the request.");
        }

        return command;
    }

    public Task<AccountQueueReadResult> ReadResultAsync(
        Guid requestId,
        string expectedEmail,
        CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("The queue request ID is required.", nameof(requestId));
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedEmail);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var snapshot = SafeAccountQueueResultFile.Read(
                _rootPath,
                $"{requestId:D}.json",
                _operations);
            if (snapshot.Status == ResultSnapshotReadStatus.Pending)
            {
                return Task.FromResult(AccountQueueReadResult.Pending);
            }

            if (snapshot.Status != ResultSnapshotReadStatus.Ready || snapshot.Bytes is null)
            {
                return Task.FromResult(AccountQueueReadResult.Invalid);
            }

            var result = JsonSerializer.Deserialize<AccountCommandResult>(snapshot.Bytes, JsonOptions);
            if (result is null || result.Id != requestId)
            {
                return Task.FromResult(AccountQueueReadResult.Invalid);
            }

            var normalizedExpectedEmail = expectedEmail.Trim().ToLowerInvariant();
            var normalizedResultEmail = result.Email?.Trim().ToLowerInvariant() ?? string.Empty;
            if (result.Status != AccountCommandStatus.Rejected && normalizedResultEmail.Length == 0)
            {
                return Task.FromResult(AccountQueueReadResult.Invalid);
            }

            if (result.Status != AccountCommandStatus.Rejected &&
                !string.Equals(normalizedResultEmail, normalizedExpectedEmail, StringComparison.Ordinal))
            {
                return Task.FromResult(AccountQueueReadResult.Invalid);
            }

            return Task.FromResult(AccountQueueReadResult.Ready(result with { Email = normalizedResultEmail }));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return Task.FromResult(AccountQueueReadResult.Invalid);
        }
    }

    public bool IsRequestDefinitelyAbsent(Guid requestId)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("The queue request ID is required.", nameof(requestId));

        try
        {
            return AccountQueueAbsenceInspector.IsDefinitelyAbsent(
                _rootPath,
                requestId,
                _operations);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
        {
            return false;
        }
    }

    private static void EnsureRealDirectory(string path, bool create)
    {
        if (Path.Exists(path))
        {
            RejectLink(path);
            if (!Directory.Exists(path))
            {
                throw new InvalidDataException("The account queue path must be a directory.");
            }

            return;
        }

        RejectLink(path);
        if (!create) throw new DirectoryNotFoundException("The account queue directory is missing.");
        Directory.CreateDirectory(path);
        RejectLink(path);
    }

    private static void RejectLink(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            throw new InvalidDataException("Symbolic links are not permitted in the account queue path.");
        }

        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Reparse points are not permitted in the account queue path.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void DeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}

/// <summary>Describes whether atomic publication was durably confirmed.</summary>
public enum AccountQueuePublishOutcome
{
    /// <summary>The final file is visible and its containing directory was durably synchronized.</summary>
    Published,

    /// <summary>
    /// The final file is visible, but directory durability could not be confirmed after the rename.
    /// Callers must retain <see cref="AccountQueuePublishResult.RequestId"/> and reconcile that request;
    /// they must not publish a replacement command with a new ID.
    /// </summary>
    Uncertain
}

/// <summary>
/// Returns the signed command and stable correlation ID for both confirmed and uncertain publication.
/// </summary>
public sealed record AccountQueuePublishResult(
    Guid RequestId,
    AccountQueuePublishOutcome Outcome,
    AccountCommand Command);

internal sealed class AccountQueueClientOperations
{
    internal static AccountQueueClientOperations Default { get; } = new();

    internal Func<bool> IsLinux { get; init; } = OperatingSystem.IsLinux;
    internal Action<string> SyncDirectory { get; init; } = AccountQueueDirectoryDurability.Sync;
    internal Action? BeforeCommandRename { get; init; }
    internal Action? BeforeResultOpen { get; init; }
    internal Action<int>? AfterResultOpenDescriptor { get; init; }
    internal Action? AfterResultInitialLengthObserved { get; init; }
    internal Action<string>? BeforeAbsentDirectoryOpen { get; init; }
    internal Action<string>? AfterAbsentDirectoryOpen { get; init; }
}

internal static class AccountQueueAbsenceInspector
{
    private const int O_RDONLY = 0;
    private const int O_CLOEXEC = 0x80000;
    private const int O_DIRECTORY = 0x10000;
    private const int O_NOFOLLOW = 0x20000;
    private const int ENOENT = 2;
    private static readonly string[] DirectoryNames =
    [
        "incoming", "processing", "results", "processed", "rejected", "uncertain", "duplicates"
    ];

    internal static bool IsDefinitelyAbsent(
        string rootPath,
        Guid requestId,
        AccountQueueClientOperations operations)
    {
        var rootDescriptor = open(rootPath, O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
        if (rootDescriptor < 0) return false;
        using var rootHandle = new SafeUnixFileDescriptor(rootDescriptor);
        if (!DescriptorStillNames(rootDescriptor, rootPath)) return false;

        var requestPrefix = requestId.ToString("D");
        foreach (var directoryName in DirectoryNames)
        {
            operations.BeforeAbsentDirectoryOpen?.Invoke(directoryName);
            var directoryDescriptor = openat(
                rootDescriptor,
                directoryName,
                O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
            if (directoryDescriptor < 0)
            {
                if (Marshal.GetLastPInvokeError() == ENOENT) continue;
                return false;
            }

            using var directoryHandle = new SafeUnixFileDescriptor(directoryDescriptor);
            operations.AfterAbsentDirectoryOpen?.Invoke(directoryName);
            var expectedDirectoryPath = Path.Combine(rootPath, directoryName);
            if (!DescriptorStillNames(directoryDescriptor, expectedDirectoryPath)) return false;
            if (ContainsRequestArtifact(directoryDescriptor, requestPrefix)) return false;
            if (!DescriptorStillNames(directoryDescriptor, expectedDirectoryPath)) return false;
        }

        return DescriptorStillNames(rootDescriptor, rootPath);
    }

    private static bool ContainsRequestArtifact(int directoryDescriptor, string requestPrefix)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries($"/proc/self/fd/{directoryDescriptor}"))
            {
                if (Path.GetFileName(entry).StartsWith(requestPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return true;
        }
    }

    private static bool DescriptorStillNames(int descriptor, string expectedPath)
    {
        var buffer = new byte[4096];
        var length = readlink($"/proc/self/fd/{descriptor}", buffer, (nuint)buffer.Length);
        if (length <= 0 || length >= buffer.Length) return false;
        var actualPath = System.Text.Encoding.UTF8.GetString(buffer, 0, (int)length);
        return string.Equals(
            Path.GetFullPath(actualPath),
            Path.GetFullPath(expectedPath),
            StringComparison.Ordinal);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int openat(int directoryDescriptor, string path, int flags);

    [DllImport("libc", EntryPoint = "readlink", SetLastError = true)]
    private static extern nint readlink(string path, byte[] buffer, nuint bufferSize);
}

internal static class AccountQueueDirectoryDurability
{
    private const int O_RDONLY = 0;
    private const int O_CLOEXEC = 0x80000;
    private const int O_DIRECTORY = 0x10000;

    internal static void Sync(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "The account queue integration is supported only on Linux.");
        }

        var descriptor = open(path, O_RDONLY | O_DIRECTORY | O_CLOEXEC);
        if (descriptor < 0) throw CreateIOException("open");
        using var handle = new SafeUnixFileDescriptor(descriptor);
        if (fsync(handle) != 0) throw CreateIOException("fsync");
    }

    private static IOException CreateIOException(string operation) =>
        new(
            $"Account queue directory durability operation {operation} failed.",
            new Win32Exception(Marshal.GetLastPInvokeError()));

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(SafeUnixFileDescriptor descriptor);
}

public enum AccountQueueReadStatus
{
    Pending,
    Ready,
    Invalid
}

public sealed record AccountQueueReadResult(
    AccountQueueReadStatus Status,
    AccountCommandResult? Result,
    string? ErrorCode)
{
    public static AccountQueueReadResult Pending { get; } = new(AccountQueueReadStatus.Pending, null, null);
    public static AccountQueueReadResult Invalid { get; } = new(AccountQueueReadStatus.Invalid, null, "invalid-result");
    public static AccountQueueReadResult Ready(AccountCommandResult result) =>
        new(AccountQueueReadStatus.Ready, result, null);
}
