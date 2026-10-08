#nullable enable

using AccountPortal.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Server.AccountQueue;

public sealed class AccountCommandProcessor
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _keyBase64;
    private readonly IAccountCommandExecutor _executor;
    private readonly Action<string>? _logger;
    private readonly IQueueDurability _durability;
    private readonly int _maxScanEntries;
    private readonly TimeSpan _scanTimeBudget;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, IEnumerable<string>> _enumerateEntries;

    public AccountCommandProcessor(
        string root,
        string keyBase64,
        IAccountCommandExecutor executor,
        Action<string>? logger = null,
        IQueueDurability? durability = null,
        int maxScanEntries = 2000,
        TimeSpan? scanTimeBudget = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ValidateKey(keyBase64);
        if (maxScanEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxScanEntries));

        var effectiveTimeBudget = scanTimeBudget ?? TimeSpan.FromMilliseconds(100);
        if (effectiveTimeBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(scanTimeBudget));
        }

        Paths = new QueuePaths(root);
        _keyBase64 = keyBase64;
        _executor = executor;
        _logger = logger;
        _durability = durability ?? new QueueDurability();
        _maxScanEntries = maxScanEntries;
        _scanTimeBudget = effectiveTimeBudget;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _enumerateEntries = Directory.EnumerateFileSystemEntries;
    }

    internal AccountCommandProcessor(
        string root,
        string keyBase64,
        IAccountCommandExecutor executor,
        int maxScanEntries,
        TimeSpan scanTimeBudget,
        Func<string, IEnumerable<string>> enumerateEntries)
        : this(
            root,
            keyBase64,
            executor,
            maxScanEntries: maxScanEntries,
            scanTimeBudget: scanTimeBudget)
    {
        _enumerateEntries = enumerateEntries ?? throw new ArgumentNullException(nameof(enumerateEntries));
    }

    public QueuePaths Paths { get; }

    public void ProcessAvailable(DateTimeOffset now, int maxCommands = 20)
    {
        if (maxCommands <= 0) return;

        var scan = new ScanBudget(_maxScanEntries, _scanTimeBudget, _timeProvider);
        var remainingCommands = maxCommands;

        foreach (var processingPath in CollectCandidates(Paths.Processing, scan))
        {
            if (remainingCommands-- <= 0) return;
            RecoverProcessing(processingPath);
        }

        if (remainingCommands <= 0 || scan.IsExhausted) return;

        foreach (var incomingPath in CollectCandidates(Paths.Incoming, scan))
        {
            if (remainingCommands-- <= 0) return;
            ProcessIncoming(incomingPath, now);
        }
    }

    private IReadOnlyList<string> CollectCandidates(string directory, ScanBudget scan)
    {
        var candidates = new List<string>();
        using var enumerator = _enumerateEntries(directory).GetEnumerator();
        while (!scan.IsExhausted && enumerator.MoveNext())
        {
            scan.ConsumeEntry();
            var path = enumerator.Current;
            if (string.Equals(Path.GetExtension(path), ".json", StringComparison.Ordinal))
            {
                candidates.Add(path);
            }
            else
            {
                TryMoveToUnique(path, Paths.Duplicates, Path.GetFileName(path), requestId: null);
            }
        }

        candidates.Sort((left, right) => StringComparer.Ordinal.Compare(Path.GetFileName(left), Path.GetFileName(right)));
        return candidates;
    }

    private void RecoverProcessing(string processingPath)
    {
        var fileName = Path.GetFileName(processingPath);
        var requestId = ParseFileId(fileName);
        var destinationDirectory = Paths.Uncertain;

        if (requestId is { } id && File.Exists(ResultPath(id)))
        {
            destinationDirectory = ReadPublishedResultStatus(id) == AccountCommandStatus.Rejected
                ? Paths.Rejected
                : Paths.Processed;
        }

        try
        {
            MoveToUniqueDurably(processingPath, destinationDirectory, fileName);
        }
        catch (FileNotFoundException)
        {
            // Another processor won the recovery race.
        }
        catch (Exception exception)
        {
            LogCommandFailure(requestId, exception);
        }
    }

    private AccountCommandStatus? ReadPublishedResultStatus(Guid requestId)
    {
        try
        {
            using var stream = new FileStream(ResultPath(requestId), FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<AccountCommandResult>(stream, JsonOptions)?.Status;
        }
        catch (Exception exception)
        {
            LogCommandFailure(requestId, exception);
            return null;
        }
    }

    private void ProcessIncoming(string incomingPath, DateTimeOffset now)
    {
        var fileName = Path.GetFileName(incomingPath);
        var requestId = ParseFileId(fileName);
        if (requestId is null)
        {
            MoveMalformedFileToRejected(incomingPath, fileName);
            return;
        }

        if (HasTerminalOrQuarantinedArtifact(requestId.Value))
        {
            TryMoveToUnique(incomingPath, Paths.Duplicates, fileName, requestId);
            return;
        }

        var processingPath = Path.Combine(Paths.Processing, fileName);
        try
        {
            MoveExactDurably(incomingPath, processingPath);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception exception)
        {
            LogCommandFailure(requestId, exception);
            return;
        }

        AccountCommand? command;
        try
        {
            var snapshot = SafeQueueFile.ReadSnapshot(processingPath);
            command = JsonSerializer.Deserialize<AccountCommand>(snapshot, JsonOptions);
        }
        catch (QueueRequestException exception)
        {
            Reject(processingPath, requestId.Value, exception.Code, now);
            return;
        }
        catch (JsonException)
        {
            Reject(processingPath, requestId.Value, "malformed-request", now);
            return;
        }
        catch (NotSupportedException)
        {
            Reject(processingPath, requestId.Value, "malformed-request", now);
            return;
        }
        catch (Exception exception)
        {
            HandlePreExecutionFailure(processingPath, requestId.Value, exception);
            return;
        }

        if (command is null)
        {
            Reject(processingPath, requestId.Value, "malformed-request", now);
            return;
        }

        if (command.Id != requestId.Value)
        {
            Reject(processingPath, requestId.Value, "id-mismatch", now);
            return;
        }

        try
        {
            command.Validate();
        }
        catch (InvalidDataException)
        {
            Reject(processingPath, requestId.Value, "malformed-request", now);
            return;
        }
        catch (Exception exception)
        {
            HandlePreExecutionFailure(processingPath, requestId.Value, exception);
            return;
        }

        var minimumIssuedAt = now.Subtract(MaximumAge).ToUnixTimeSeconds();
        var maximumIssuedAt = now.Add(MaximumFutureSkew).ToUnixTimeSeconds();
        if (command.IssuedAtUnixSeconds < minimumIssuedAt)
        {
            Reject(processingPath, requestId.Value, "expired-request", now);
            return;
        }

        if (command.IssuedAtUnixSeconds > maximumIssuedAt)
        {
            Reject(processingPath, requestId.Value, "future-request", now);
            return;
        }

        if (!AccountCommandSigner.Verify(command, _keyBase64))
        {
            Reject(processingPath, requestId.Value, "invalid-signature", now);
            return;
        }

        ProcessValidated(processingPath, requestId.Value, command, now);
    }

    private bool HasTerminalOrQuarantinedArtifact(Guid requestId)
    {
        var fileName = $"{requestId:D}.json";
        return File.Exists(ResultPath(requestId)) ||
               Path.Exists(Path.Combine(Paths.Processed, fileName)) ||
               Path.Exists(Path.Combine(Paths.Rejected, fileName)) ||
               Path.Exists(Path.Combine(Paths.Uncertain, fileName));
    }

    private void ProcessValidated(
        string processingPath,
        Guid requestId,
        AccountCommand command,
        DateTimeOffset now)
    {
        AccountCommandResult result;
        try
        {
            result = _executor.Execute(command, now)
                ?? throw new InvalidDataException("The account command executor returned no result.");
            if (result.Id != requestId)
            {
                throw new InvalidDataException("The account command executor returned a mismatched result ID.");
            }
        }
        catch (Exception exception)
        {
            TryMoveToUnique(processingPath, Paths.Uncertain, Path.GetFileName(processingPath), requestId);
            LogCommandFailure(requestId, exception);
            return;
        }

        try
        {
            WriteResultAtomically(ResultPath(requestId), result);
        }
        catch (Exception exception)
        {
            TryMoveToUnique(processingPath, Paths.Uncertain, Path.GetFileName(processingPath), requestId);
            LogCommandFailure(requestId, exception);
            return;
        }

        try
        {
            MoveToUniqueDurably(processingPath, Paths.Processed, Path.GetFileName(processingPath));
        }
        catch (Exception exception)
        {
            // The durable result is authoritative. Leave/restore the request in processing for recovery.
            LogCommandFailure(requestId, exception);
        }
    }

    private void Reject(string processingPath, Guid requestId, string code, DateTimeOffset now)
    {
        try
        {
            var result = new AccountCommandResult(
                requestId,
                AccountCommandStatus.Rejected,
                code,
                string.Empty,
                now);
            WriteResultAtomically(ResultPath(requestId), result);
        }
        catch (Exception exception)
        {
            TryMoveToUnique(processingPath, Paths.Uncertain, Path.GetFileName(processingPath), requestId);
            LogCommandFailure(requestId, exception);
            return;
        }

        try
        {
            MoveToUniqueDurably(processingPath, Paths.Rejected, Path.GetFileName(processingPath));
        }
        catch (Exception exception)
        {
            // Recovery will observe the durable rejection result and retry the archive only.
            LogCommandFailure(requestId, exception);
        }
    }

    private void HandlePreExecutionFailure(string processingPath, Guid requestId, Exception exception)
    {
        TryMoveToUnique(processingPath, Paths.Uncertain, Path.GetFileName(processingPath), requestId);
        LogCommandFailure(requestId, exception);
    }

    private void MoveMalformedFileToRejected(string incomingPath, string fileName)
    {
        try
        {
            MoveToUniqueDurably(incomingPath, Paths.Rejected, fileName);
        }
        catch (FileNotFoundException)
        {
        }
        catch (Exception exception)
        {
            LogSystemFailure("reject-invalid-filename", exception);
        }
    }

    private void WriteResultAtomically(string resultPath, AccountCommandResult result)
    {
        var temporaryPath = TemporaryResultPath(resultPath);
        var temporaryCreated = false;
        var resultPublished = false;

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                temporaryCreated = true;
                JsonSerializer.Serialize(stream, result, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, resultPath);
            temporaryCreated = false;
            resultPublished = true;
            _durability.SyncDirectory(Paths.Results);
        }
        catch
        {
            if (temporaryCreated) DeleteFileBestEffort(temporaryPath);
            if (resultPublished)
            {
                DeleteFileBestEffort(resultPath);
                TrySyncDirectory(Paths.Results);
            }

            throw;
        }
    }

    private void MoveExactDurably(string source, string destination)
    {
        MovePath(source, destination);
        SyncMoveOrRollback(source, destination);
    }

    private string MoveToUniqueDurably(string source, string destinationDirectory, string baseFileName)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var destination = Path.Combine(
                destinationDirectory,
                attempt == 0 ? baseFileName : DuplicateFileName(baseFileName));
            try
            {
                MovePath(source, destination);
                SyncMoveOrRollback(source, destination);
                return destination;
            }
            catch (IOException) when (Path.Exists(destination) && Path.Exists(source))
            {
                // A collision appeared between selection and rename; select another unique name.
            }
        }

        throw new IOException("Unable to select a collision-safe account queue archive name.");
    }

    private void SyncMoveOrRollback(string source, string destination)
    {
        var sourceDirectory = Path.GetDirectoryName(source)!;
        var destinationDirectory = Path.GetDirectoryName(destination)!;
        try
        {
            _durability.SyncDirectory(sourceDirectory);
            if (!string.Equals(sourceDirectory, destinationDirectory, StringComparison.Ordinal))
            {
                _durability.SyncDirectory(destinationDirectory);
            }
        }
        catch
        {
            try
            {
                if (!Path.Exists(source) && Path.Exists(destination))
                {
                    MovePath(destination, source);
                    TrySyncDirectory(destinationDirectory);
                    TrySyncDirectory(sourceDirectory);
                }
            }
            catch
            {
                // Preserve the original durability failure; recovery inspects both locations.
            }

            throw;
        }
    }

    private void TryMoveToUnique(string source, string destinationDirectory, string baseFileName, Guid? requestId)
    {
        try
        {
            MoveToUniqueDurably(source, destinationDirectory, baseFileName);
        }
        catch (FileNotFoundException)
        {
        }
        catch (Exception exception)
        {
            LogCommandFailure(requestId, exception);
        }
    }

    private void TrySyncDirectory(string path)
    {
        try
        {
            _durability.SyncDirectory(path);
        }
        catch
        {
        }
    }

    private static string DuplicateFileName(string baseFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(baseFileName);
        var extension = Path.GetExtension(baseFileName);
        return $"{stem}.duplicate-{Guid.NewGuid():N}{extension}";
    }

    private static void MovePath(string source, string destination)
    {
        if (Directory.Exists(source) && !File.Exists(source))
        {
            Directory.Move(source, destination);
            return;
        }

        File.Move(source, destination);
    }

    private string ResultPath(Guid requestId) =>
        Path.Combine(Paths.Results, $"{requestId:D}.json");

    private static string TemporaryResultPath(string resultPath) => resultPath + ".tmp";

    private static Guid? ParseFileId(string fileName)
    {
        if (!string.Equals(Path.GetExtension(fileName), ".json", StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileNameWithoutExtension(fileName), "D", out var id))
        {
            return null;
        }

        return id;
    }

    private static void ValidateKey(string keyBase64)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(keyBase64);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            throw new InvalidDataException(
                "The account command HMAC key must be valid Base64 and decode to exactly 32 bytes.",
                exception);
        }

        try
        {
            if (key.Length != 32)
            {
                throw new InvalidDataException(
                    "The account command HMAC key must decode to exactly 32 bytes.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort cleanup. The original failure remains authoritative.
        }
    }

    private void LogCommandFailure(Guid? requestId, Exception exception)
    {
        TryLog(
            $"Account queue request {(requestId?.ToString("D") ?? "unknown")} failed with {exception.GetType().Name}.");
    }

    private void LogSystemFailure(string context, Exception exception)
    {
        TryLog($"Account queue {context} failed with {exception.GetType().Name}.");
    }

    private void TryLog(string message)
    {
        try
        {
            _logger?.Invoke(message);
        }
        catch
        {
            // Logging must never stop queue recovery or processing.
        }
    }

    private sealed class ScanBudget(int remainingEntries, TimeSpan timeBudget, TimeProvider timeProvider)
    {
        private readonly long _startedAt = timeProvider.GetTimestamp();

        public bool IsExhausted =>
            remainingEntries <= 0 || timeProvider.GetElapsedTime(_startedAt) >= timeBudget;

        public void ConsumeEntry()
        {
            if (remainingEntries > 0) remainingEntries--;
        }
    }
}
