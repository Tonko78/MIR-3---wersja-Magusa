using System.Text.Json;
using AccountPortal.Contracts;
using Server.AccountQueue;

namespace ServerCore.Tests.AccountQueue;

public sealed class AccountCommandProcessorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly string KeyBase64 = Convert.ToBase64String(
        Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "zircon-account-queue-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProcessAvailable_ValidSignedCommandExecutesOnceWritesResultAndMovesToProcessed()
    {
        var executor = new FakeExecutor();
        var processor = CreateProcessor(executor);
        var command = WriteSignedCommand(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        processor.ProcessAvailable(Now);

        Assert.Equal(command.Id, Assert.Single(executor.Commands).Id);
        Assert.False(File.Exists(Incoming(command.Id)));
        Assert.False(File.Exists(Processing(command.Id)));
        Assert.True(File.Exists(Processed(command.Id)));
        var result = ReadResult(command.Id);
        Assert.Equal(command.Id, result.Id);
        Assert.Equal(AccountCommandStatus.Success, result.Status);
        Assert.Equal("executed", result.Code);
    }

    [Fact]
    public void ProcessAvailable_InvalidSignatureRejectsWithoutExecuting()
    {
        var executor = new FakeExecutor();
        var processor = CreateProcessor(executor);
        var command = CreateCommand(Guid.Parse("22222222-2222-2222-2222-222222222222"), Now);
        AccountCommandSigner.Sign(command, KeyBase64);
        command.SignatureBase64 = Convert.ToBase64String(new byte[32]);
        WriteIncoming(command);

        processor.ProcessAvailable(Now);

        Assert.Empty(executor.Commands);
        AssertRejected(command.Id, "invalid-signature");
    }

    [Theory]
    [InlineData(-301, "expired-request")]
    [InlineData(61, "future-request")]
    public void ProcessAvailable_OutOfWindowTimestampRejectsWithoutExecuting(int secondsFromNow, string expectedCode)
    {
        var executor = new FakeExecutor();
        var processor = CreateProcessor(executor);
        var command = WriteSignedCommand(
            Guid.Parse(secondsFromNow < 0
                ? "33333333-3333-3333-3333-333333333333"
                : "44444444-4444-4444-4444-444444444444"),
            Now.AddSeconds(secondsFromNow));

        processor.ProcessAvailable(Now);

        Assert.Empty(executor.Commands);
        AssertRejected(command.Id, expectedCode);
    }

    [Theory]
    [InlineData(long.MinValue, "expired-request")]
    [InlineData(long.MaxValue, "future-request")]
    public void ProcessAvailable_ExtremeTimestampRejectsWithoutEscaping(long issuedAt, string expectedCode)
    {
        var executor = new FakeExecutor();
        var processor = CreateProcessor(executor);
        var id = issuedAt < 0
            ? Guid.Parse("33333333-3333-3333-3333-333333333334")
            : Guid.Parse("44444444-4444-4444-4444-444444444445");
        var command = new AccountCommand
        {
            Id = id,
            Type = AccountCommandType.ActivateAccount,
            Email = "player@example.com",
            IssuedAtUnixSeconds = issuedAt,
            Nonce = $"nonce-{id:D}"
        };
        AccountCommandSigner.Sign(command, KeyBase64);
        WriteIncoming(command);

        var exception = Record.Exception(() => processor.ProcessAvailable(Now));

        Assert.Null(exception);
        Assert.Empty(executor.Commands);
        AssertRejected(id, expectedCode);
    }

    [Fact]
    public void ProcessAvailable_BodyIdMismatchRejectsFilenameIdWithoutExecuting()
    {
        var executor = new FakeExecutor();
        var processor = CreateProcessor(executor);
        var filenameId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var command = CreateCommand(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), Now);
        AccountCommandSigner.Sign(command, KeyBase64);
        WriteIncoming(command, filenameId);

        processor.ProcessAvailable(Now);

        Assert.Empty(executor.Commands);
        AssertRejected(filenameId, "id-mismatch");
    }

    [Fact]
    public void ProcessAvailable_ExistingResultPreventsReplayAndMovesIncomingToDuplicates()
    {
        var executor = new FakeExecutor();
        var processor = CreateProcessor(executor);
        var command = WriteSignedCommand(Guid.Parse("66666666-6666-6666-6666-666666666666"));
        WriteResult(new AccountCommandResult(
            command.Id,
            AccountCommandStatus.Success,
            "already-complete",
            command.Email,
            Now));

        processor.ProcessAvailable(Now);

        Assert.Empty(executor.Commands);
        Assert.False(File.Exists(Incoming(command.Id)));
        Assert.Single(Directory.EnumerateFiles(Duplicates(), "*.json"));
        Assert.Equal("already-complete", ReadResult(command.Id).Code);
    }

    [Fact]
    public void ProcessAvailable_ResubmittedUncertainRequestIsQuarantinedAsDuplicateWithoutExecution()
    {
        var executor = new FakeExecutor();
        var processor = CreateProcessor(executor);
        var command = WriteSignedCommand(Guid.Parse("66666666-6666-6666-6666-666666666667"));
        var original = JsonSerializer.Serialize(command);
        File.WriteAllText(Uncertain(command.Id), original);

        processor.ProcessAvailable(Now);

        Assert.Empty(executor.Commands);
        Assert.Equal(original, File.ReadAllText(Uncertain(command.Id)));
        Assert.False(File.Exists(Incoming(command.Id)));
        var duplicate = Assert.Single(Directory.EnumerateFiles(Duplicates(), "*.json"));
        Assert.Equal(original, File.ReadAllText(duplicate));
    }

    [Fact]
    public void ProcessAvailable_MalformedJsonWithGuidFilenameRejectsWithoutThrowing()
    {
        var executor = new FakeExecutor();
        var processor = CreateProcessor(executor);
        var id = Guid.Parse("77777777-7777-7777-7777-777777777777");
        File.WriteAllText(Incoming(id), "{ definitely-not-json }");

        var exception = Record.Exception(() => processor.ProcessAvailable(Now));

        Assert.Null(exception);
        Assert.Empty(executor.Commands);
        AssertRejected(id, "malformed-request");
    }

    [Fact]
    public void ProcessAvailable_ExecutorExceptionMovesToUncertainLogsSafelyAndContinues()
    {
        const string secretMessage = "exception-secret-sentinel";
        const string secretEmail = "private-email@example.com";
        const string secretHash = "private-hash-sentinel";
        var failedId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var successfulId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var logs = new List<string>();
        var executor = new FakeExecutor(command =>
        {
            if (command.Id == failedId) throw new InvalidOperationException(secretMessage);
            return Success(command);
        });
        var processor = CreateProcessor(executor, logs.Add);
        var failed = AccountCommand.Create(
            failedId,
            AccountCommandType.ActivateAccount,
            secretEmail,
            secretHash,
            Now,
            $"nonce-{failedId:D}");
        AccountCommandSigner.Sign(failed, KeyBase64);
        var secretSignature = failed.SignatureBase64;
        WriteIncoming(failed);
        WriteSignedCommand(successfulId);

        processor.ProcessAvailable(Now);

        Assert.Equal(2, executor.Commands.Count);
        Assert.True(File.Exists(Uncertain(failedId)));
        Assert.False(File.Exists(Result(failedId)));
        Assert.True(File.Exists(Processed(successfulId)));
        Assert.True(File.Exists(Result(successfulId)));
        var log = Assert.Single(logs);
        Assert.Contains(failedId.ToString("D"), log, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), log, StringComparison.Ordinal);
        Assert.DoesNotContain(secretMessage, log, StringComparison.Ordinal);
        Assert.DoesNotContain(secretEmail, log, StringComparison.Ordinal);
        Assert.DoesNotContain(secretHash, log, StringComparison.Ordinal);
        Assert.DoesNotContain(secretSignature, log, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessAvailable_WhenLoggerThrowsStillContinuesOtherRequests()
    {
        var failedId = Guid.Parse("10000000-0000-0000-0000-000000000003");
        var successfulId = Guid.Parse("10000000-0000-0000-0000-000000000004");
        var executor = new FakeExecutor(command =>
        {
            if (command.Id == failedId) throw new InvalidOperationException("executor-failure");
            return Success(command);
        });
        var processor = CreateProcessor(executor, _ => throw new InvalidOperationException("logger-failure"));
        WriteSignedCommand(failedId);
        WriteSignedCommand(successfulId);

        var exception = Record.Exception(() => processor.ProcessAvailable(Now));

        Assert.Null(exception);
        Assert.True(File.Exists(Uncertain(failedId)));
        Assert.True(File.Exists(Processed(successfulId)));
    }

    [Fact]
    public void ProcessAvailable_RecoversProcessingFilesWithoutReexecution()
    {
        var executor = new FakeExecutor();
        var processor = CreateProcessor(executor);
        var completed = CreateCommand(Guid.Parse("88888888-8888-8888-8888-888888888888"), Now);
        var uncertain = CreateCommand(Guid.Parse("99999999-9999-9999-9999-999999999999"), Now);
        AccountCommandSigner.Sign(completed, KeyBase64);
        AccountCommandSigner.Sign(uncertain, KeyBase64);
        WriteProcessing(completed);
        WriteProcessing(uncertain);
        WriteResult(new AccountCommandResult(
            completed.Id,
            AccountCommandStatus.Success,
            "executed",
            completed.Email,
            Now));

        processor.ProcessAvailable(Now);

        Assert.Empty(executor.Commands);
        Assert.True(File.Exists(Processed(completed.Id)));
        Assert.True(File.Exists(Uncertain(uncertain.Id)));
        Assert.False(File.Exists(Processing(completed.Id)));
        Assert.False(File.Exists(Processing(uncertain.Id)));
    }

    [Fact]
    public void ProcessAvailable_AtomicResultLeavesCompleteJsonAndNoTemporaryFiles()
    {
        var processor = CreateProcessor(new FakeExecutor());
        var command = WriteSignedCommand(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        processor.ProcessAvailable(Now);

        var json = File.ReadAllText(Result(command.Id));
        var result = JsonSerializer.Deserialize<AccountCommandResult>(json);
        Assert.NotNull(result);
        Assert.Equal(command.Id, result.Id);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void ProcessAvailable_ProcessedCollisionDuringExecutionPreservesResultAndAuditFilesWithoutReplay()
    {
        var id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbc");
        const string existingAudit = "existing-audit-evidence";
        var executor = new FakeExecutor(command =>
        {
            File.WriteAllText(Processed(command.Id), existingAudit);
            return Success(command);
        });
        var processor = CreateProcessor(executor);
        WriteSignedCommand(id);

        processor.ProcessAvailable(Now);
        processor.ProcessAvailable(Now);

        Assert.Single(executor.Commands);
        Assert.True(File.Exists(Result(id)));
        Assert.Equal(existingAudit, File.ReadAllText(Processed(id)));
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(_root, "processed"), "*.json").Count());
        Assert.False(File.Exists(Processing(id)));
        Assert.False(File.Exists(Uncertain(id)));
    }

    [Fact]
    public void ProcessAvailable_RejectionResultWriteFailureDoesNotFinalizeRejectedArchive()
    {
        var executor = new FakeExecutor();
        var processor = CreateProcessor(executor);
        var id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbd");
        File.WriteAllText(Incoming(id), "{ malformed }");
        Directory.CreateDirectory(Result(id) + ".tmp");

        processor.ProcessAvailable(Now);

        Assert.Empty(executor.Commands);
        Assert.False(File.Exists(Result(id)));
        Assert.False(File.Exists(Rejected(id)));
        Assert.True(File.Exists(Uncertain(id)) || File.Exists(Processing(id)));
    }

    [Fact]
    public void ProcessAvailable_SharesMaximumAcrossRecoveryAndIncoming()
    {
        var executor = new FakeExecutor();
        var processor = CreateProcessor(executor);
        var first = CreateCommand(Guid.Parse("cccccccc-0000-0000-0000-000000000001"), Now);
        var second = CreateCommand(Guid.Parse("cccccccc-0000-0000-0000-000000000002"), Now);
        AccountCommandSigner.Sign(first, KeyBase64);
        AccountCommandSigner.Sign(second, KeyBase64);
        WriteProcessing(first);
        WriteProcessing(second);
        var incoming = WriteSignedCommand(Guid.Parse("cccccccc-0000-0000-0000-000000000003"));

        processor.ProcessAvailable(Now, maxCommands: 1);

        Assert.Empty(executor.Commands);
        Assert.True(File.Exists(Incoming(incoming.Id)));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "processing"), "*.json"));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "uncertain"), "*.json"));
    }

    [Fact]
    public void ProcessAvailable_OversizedRequestRejectsBeforeDeserialization()
    {
        var executor = new FakeExecutor();
        var processor = CreateProcessor(executor);
        var id = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
        File.WriteAllBytes(Incoming(id), Enumerable.Repeat((byte)' ', 64 * 1024 + 1).ToArray());

        processor.ProcessAvailable(Now);

        Assert.Empty(executor.Commands);
        AssertRejected(id, "request-too-large");
    }

    [Fact]
    public void ProcessAvailable_SymlinkRequestIsRejectedWithoutFollowingTarget()
    {
        if (!OperatingSystem.IsLinux()) return;

        var executor = new FakeExecutor();
        var processor = CreateProcessor(executor);
        var id = Guid.Parse("dddddddd-0000-0000-0000-000000000002");
        var target = Path.Combine(_root, "outside-request.json");
        var command = CreateCommand(id, Now);
        AccountCommandSigner.Sign(command, KeyBase64);
        var targetContents = JsonSerializer.Serialize(command);
        File.WriteAllText(target, targetContents);
        File.CreateSymbolicLink(Incoming(id), target);

        processor.ProcessAvailable(Now);

        Assert.Empty(executor.Commands);
        Assert.Equal(targetContents, File.ReadAllText(target));
        AssertRejected(id, "unsafe-request-file");
    }

    [Fact]
    public void ProcessAvailable_HonorsMaximumAndUsesOrdinalFilenameOrder()
    {
        var executor = new FakeExecutor();
        var processor = CreateProcessor(executor);
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var third = Guid.Parse("00000000-0000-0000-0000-000000000003");
        WriteSignedCommand(third);
        WriteSignedCommand(first);
        WriteSignedCommand(second);

        processor.ProcessAvailable(Now, maxCommands: 2);

        Assert.Equal(new[] { first, second }, executor.Commands.Select(command => command.Id));
        Assert.True(File.Exists(Incoming(third)));
        Assert.True(File.Exists(Processed(first)));
        Assert.True(File.Exists(Processed(second)));
    }

    [Fact]
    public void ProcessAvailable_BoundsIncomingScanAndOrdersScannedCandidates()
    {
        var executor = new FakeExecutor();
        var processor = new AccountCommandProcessor(
            _root,
            KeyBase64,
            executor,
            maxScanEntries: 2,
            scanTimeBudget: TimeSpan.FromSeconds(1));
        WriteSignedCommand(Guid.Parse("eeeeeeee-0000-0000-0000-000000000003"));
        WriteSignedCommand(Guid.Parse("eeeeeeee-0000-0000-0000-000000000001"));
        WriteSignedCommand(Guid.Parse("eeeeeeee-0000-0000-0000-000000000002"));

        processor.ProcessAvailable(Now, maxCommands: 3);

        Assert.Equal(2, executor.Commands.Count);
        Assert.Equal(
            executor.Commands.Select(command => command.Id).OrderBy(id => $"{id:D}", StringComparer.Ordinal),
            executor.Commands.Select(command => command.Id));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "incoming"), "*.json"));
    }

    [Fact]
    public void ProcessAvailable_QuarantinesBoundedJunkAndEventuallyReachesValidRequest()
    {
        const int maxScanEntries = 2;
        var executor = new FakeExecutor();
        var processor = new AccountCommandProcessor(
            _root,
            KeyBase64,
            executor,
            maxScanEntries,
            TimeSpan.FromSeconds(1),
            directory => Directory
                .EnumerateFileSystemEntries(directory)
                .OrderBy(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(Path.GetFileName, StringComparer.Ordinal));

        for (var index = 0; index < 3; index++)
        {
            File.WriteAllText(Path.Combine(_root, "processing", $"00-processing-junk-{index:D2}.tmp"), "junk");
        }

        for (var index = 0; index < 5; index++)
        {
            File.WriteAllText(Path.Combine(_root, "incoming", $"00-incoming-junk-{index:D2}.tmp"), "junk");
        }

        var valid = WriteSignedCommand(Guid.Parse("ffffffff-0000-0000-0000-000000000001"));
        var previousDuplicateCount = 0;

        for (var tick = 0; tick < 6; tick++)
        {
            processor.ProcessAvailable(Now);

            var duplicateCount = Directory.EnumerateFileSystemEntries(Duplicates()).Count();
            Assert.InRange(duplicateCount - previousDuplicateCount, 0, maxScanEntries);
            previousDuplicateCount = duplicateCount;
        }

        Assert.Equal(valid.Id, Assert.Single(executor.Commands).Id);
        Assert.Equal(8, Directory.EnumerateFileSystemEntries(Duplicates()).Count());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "processing")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "incoming"), "*.tmp"));
    }

    [Fact]
    public void ProcessAvailable_QuarantinesBoundedGuidJsonDirectoriesAndEventuallyReachesValidRequest()
    {
        const int maxScanEntries = 2;
        var executor = new FakeExecutor();
        var processor = new AccountCommandProcessor(
            _root,
            KeyBase64,
            executor,
            maxScanEntries,
            TimeSpan.FromSeconds(1),
            directory => Directory
                .EnumerateFileSystemEntries(directory)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal));
        var directoryIds = Enumerable.Range(1, maxScanEntries + 3)
            .Select(index => Guid.Parse($"00000000-0000-0000-0000-{index:D12}"))
            .ToArray();
        foreach (var id in directoryIds)
        {
            Directory.CreateDirectory(Incoming(id));
        }

        var valid = WriteSignedCommand(Guid.Parse("ffffffff-0000-0000-0000-000000000002"));
        var previousIncomingCount = directoryIds.Length + 1;

        for (var tick = 0; tick < 3; tick++)
        {
            processor.ProcessAvailable(Now);

            var incomingCount = Directory.EnumerateFileSystemEntries(Path.Combine(_root, "incoming")).Count();
            Assert.InRange(previousIncomingCount - incomingCount, 0, maxScanEntries);
            previousIncomingCount = incomingCount;
        }

        Assert.Equal(valid.Id, Assert.Single(executor.Commands).Id);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "incoming")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "processing")));
        Assert.All(directoryIds, id =>
        {
            Assert.True(Directory.Exists(Rejected(id)));
            Assert.Equal("unsafe-request-file", ReadResult(id).Code);
        });
    }

    [Fact]
    public void ProcessAvailable_RecoversGuidJsonDirectoryFromProcessingWithoutRecurring()
    {
        var executor = new FakeExecutor();
        var processor = CreateProcessor(executor);
        var id = Guid.Parse("eeeeeeee-0000-0000-0000-000000000006");
        Directory.CreateDirectory(Processing(id));

        processor.ProcessAvailable(Now);
        processor.ProcessAvailable(Now);

        Assert.Empty(executor.Commands);
        Assert.False(Directory.Exists(Processing(id)));
        Assert.True(Directory.Exists(Uncertain(id)));
        Assert.Single(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "uncertain")));
    }

    [Fact]
    public void ProcessAvailable_SyncsClaimBeforeExecutionAndResultBeforeArchive()
    {
        var trace = new List<string>();
        var durability = new RecordingDurability(trace);
        var executor = new FakeExecutor(command =>
        {
            trace.Add("execute");
            return Success(command);
        });
        var processor = new AccountCommandProcessor(
            _root,
            KeyBase64,
            executor,
            durability: durability);
        WriteSignedCommand(Guid.Parse("eeeeeeee-0000-0000-0000-000000000004"));

        processor.ProcessAvailable(Now);

        var executeIndex = trace.IndexOf("execute");
        Assert.True(trace.IndexOf("sync:incoming") < executeIndex);
        Assert.True(trace.IndexOf("sync:processing") < executeIndex);
        var resultSyncIndex = trace.IndexOf("sync:results");
        Assert.True(resultSyncIndex > executeIndex);
        Assert.True(resultSyncIndex < trace.LastIndexOf("sync:processed"));
        Assert.True(resultSyncIndex < trace.LastIndexOf("sync:processing"));
    }

    [Fact]
    public void ProcessAvailable_ArchiveSyncFailureKeepsPublishedResultAndRecoveryDoesNotReexecute()
    {
        var id = Guid.Parse("eeeeeeee-0000-0000-0000-000000000005");
        var executor = new FakeExecutor();
        var durability = new FailOnceDurability("processed");
        var processor = new AccountCommandProcessor(
            _root,
            KeyBase64,
            executor,
            durability: durability);
        WriteSignedCommand(id);

        processor.ProcessAvailable(Now);

        Assert.Single(executor.Commands);
        Assert.True(File.Exists(Result(id)));
        Assert.True(File.Exists(Processing(id)));
        Assert.False(File.Exists(Processed(id)));

        processor.ProcessAvailable(Now);

        Assert.Single(executor.Commands);
        Assert.True(File.Exists(Result(id)));
        Assert.False(File.Exists(Processing(id)));
        Assert.True(File.Exists(Processed(id)));
    }

    [Fact]
    public void ProcessAvailable_MissingQueueDirectoriesPropagatesSystemFailure()
    {
        var processor = CreateProcessor(new FakeExecutor());
        Directory.Delete(_root, recursive: true);

        Assert.Throws<DirectoryNotFoundException>(() => processor.ProcessAvailable(Now));
    }

    [Fact]
    public void Constructor_CreatesAllQueueDirectoriesAndRejectsInvalidKeyWithoutLoggingIt()
    {
        var logs = new List<string>();
        var paths = new QueuePaths(_root);

        Assert.All(
            new[]
            {
                paths.Root,
                paths.Incoming,
                paths.Processing,
                paths.Results,
                paths.Processed,
                paths.Rejected,
                paths.Uncertain,
                paths.Duplicates
            },
            path => Assert.True(Directory.Exists(path)));

        Assert.Throws<InvalidDataException>(
            () => new AccountCommandProcessor(_root, "not-a-valid-secret-key", new FakeExecutor(), logs.Add));
        Assert.Empty(logs);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private AccountCommandProcessor CreateProcessor(FakeExecutor executor, Action<string>? logger = null)
    {
        return new AccountCommandProcessor(_root, KeyBase64, executor, logger);
    }

    private AccountCommand WriteSignedCommand(Guid id, DateTimeOffset? issuedAt = null)
    {
        var command = CreateCommand(id, issuedAt ?? Now);
        AccountCommandSigner.Sign(command, KeyBase64);
        WriteIncoming(command);
        return command;
    }

    private static AccountCommand CreateCommand(Guid id, DateTimeOffset issuedAt)
    {
        return AccountCommand.Create(
            id,
            AccountCommandType.ActivateAccount,
            "player@example.com",
            null,
            issuedAt,
            $"nonce-{id:D}");
    }

    private void WriteIncoming(AccountCommand command, Guid? filenameId = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, "incoming"));
        File.WriteAllText(Incoming(filenameId ?? command.Id), JsonSerializer.Serialize(command));
    }

    private void WriteProcessing(AccountCommand command)
    {
        Directory.CreateDirectory(Path.Combine(_root, "processing"));
        File.WriteAllText(Processing(command.Id), JsonSerializer.Serialize(command));
    }

    private void WriteResult(AccountCommandResult result)
    {
        Directory.CreateDirectory(Path.Combine(_root, "results"));
        File.WriteAllText(Result(result.Id), JsonSerializer.Serialize(result));
    }

    private AccountCommandResult ReadResult(Guid id)
    {
        return JsonSerializer.Deserialize<AccountCommandResult>(File.ReadAllText(Result(id)))!;
    }

    private void AssertRejected(Guid id, string code)
    {
        Assert.True(File.Exists(Rejected(id)));
        Assert.False(File.Exists(Processing(id)));
        var result = ReadResult(id);
        Assert.Equal(id, result.Id);
        Assert.Equal(AccountCommandStatus.Rejected, result.Status);
        Assert.Equal(code, result.Code);
    }

    private string Incoming(Guid id) => Path.Combine(_root, "incoming", $"{id:D}.json");
    private string Processing(Guid id) => Path.Combine(_root, "processing", $"{id:D}.json");
    private string Result(Guid id) => Path.Combine(_root, "results", $"{id:D}.json");
    private string Processed(Guid id) => Path.Combine(_root, "processed", $"{id:D}.json");
    private string Rejected(Guid id) => Path.Combine(_root, "rejected", $"{id:D}.json");
    private string Uncertain(Guid id) => Path.Combine(_root, "uncertain", $"{id:D}.json");
    private string Duplicates() => Path.Combine(_root, "duplicates");

    private static AccountCommandResult Success(AccountCommand command)
    {
        return new AccountCommandResult(
            command.Id,
            AccountCommandStatus.Success,
            "executed",
            command.Email,
            Now);
    }

    private sealed class FakeExecutor(Func<AccountCommand, AccountCommandResult>? execute = null)
        : IAccountCommandExecutor
    {
        public List<AccountCommand> Commands { get; } = [];

        public AccountCommandResult Execute(AccountCommand command, DateTimeOffset now)
        {
            Commands.Add(command);
            return execute?.Invoke(command) ?? Success(command);
        }
    }

    private sealed class RecordingDurability(List<string> trace) : IQueueDurability
    {
        public void SyncDirectory(string path)
        {
            trace.Add($"sync:{Path.GetFileName(path)}");
        }
    }

    private sealed class FailOnceDurability(string directoryName) : IQueueDurability
    {
        private bool _failed;

        public void SyncDirectory(string path)
        {
            if (!_failed && string.Equals(Path.GetFileName(path), directoryName, StringComparison.Ordinal))
            {
                _failed = true;
                throw new IOException("injected-directory-sync-failure");
            }
        }
    }
}
