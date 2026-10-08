using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using AccountPortal.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mir3.Web.Data;
using Mir3.Web.Domain;
using Mir3.Web.Options;
using Mir3.Web.Services;

namespace Mir3.Web.Tests;

public sealed class AccountQueueClientTests : IDisposable
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    private static readonly string KeyBase64 = Convert.ToBase64String(Key);
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mir3-web-queue-{Guid.NewGuid():N}");

    [Fact]
    public async Task WriteCommand_normalizes_email_signs_hash_only_payload_and_publishes_atomically()
    {
        var client = CreateClient();
        var requestId = Guid.NewGuid();
        var passwordHash = Enumerable.Range(0, 36).Select(value => (byte)value).ToArray();

        var publication = await client.WriteCommandAsync(
            requestId,
            AccountCommandType.CreateAccount,
            "  PLAYER@Example.TEST ",
            passwordHash,
            CancellationToken.None);

        Assert.Equal(AccountQueuePublishOutcome.Published, publication.Outcome);
        Assert.Equal(requestId, publication.RequestId);
        var command = publication.Command;
        Assert.Equal(requestId, command.Id);
        Assert.Equal(AccountCommandType.CreateAccount, command.Type);
        Assert.Equal("player@example.test", command.Email);
        Assert.Equal(Convert.ToBase64String(passwordHash), command.PasswordHashBase64);
        Assert.Equal(Now.ToUnixTimeSeconds(), command.IssuedAtUnixSeconds);
        Assert.NotEmpty(command.Nonce);
        Assert.True(AccountCommandSigner.Verify(command, KeyBase64));

        var incoming = Path.Combine(_root, "incoming");
        var publishedPath = Path.Combine(incoming, $"{requestId:D}.json");
        Assert.True(File.Exists(publishedPath));
        Assert.False(File.Exists(publishedPath + ".tmp"));
        Assert.Single(Directory.EnumerateFiles(incoming, "*.json"));

        var json = await File.ReadAllTextAsync(publishedPath, CancellationToken.None);
        Assert.DoesNotContain("correct horse battery staple", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PLAYER@Example.TEST", json, StringComparison.Ordinal);
        var persisted = JsonSerializer.Deserialize<AccountCommand>(json);
        Assert.NotNull(persisted);
        Assert.Equal(command.SignatureBase64, persisted.SignatureBase64);
        Assert.True(AccountCommandSigner.Verify(persisted, KeyBase64));
    }

    [Fact]
    public async Task Non_create_command_does_not_include_password_hash()
    {
        var client = CreateClient();

        var publication = await client.WriteCommandAsync(
            Guid.NewGuid(),
            AccountCommandType.DeactivateAccount,
            "player@example.test",
            passwordHash: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(AccountQueuePublishOutcome.Published, publication.Outcome);
        Assert.Null(publication.Command.PasswordHashBase64);
        Assert.True(AccountCommandSigner.Verify(publication.Command, KeyBase64));
    }

    [Fact]
    public async Task Reset_password_command_includes_the_new_hash()
    {
        var client = CreateClient();
        var passwordHash = Enumerable.Range(0, 36).Select(value => (byte)value).ToArray();

        var publication = await client.WriteCommandAsync(
            Guid.NewGuid(),
            AccountCommandType.ResetPassword,
            "player@example.test",
            passwordHash,
            CancellationToken.None);

        Assert.Equal(AccountQueuePublishOutcome.Published, publication.Outcome);
        Assert.Equal(Convert.ToBase64String(passwordHash), publication.Command.PasswordHashBase64);
        Assert.True(AccountCommandSigner.Verify(publication.Command, KeyBase64));
    }

    [Fact]
    public async Task WriteCommand_generates_a_unique_request_id_by_default()
    {
        var client = CreateClient();

        var first = await client.WriteCommandAsync(
            AccountCommandType.ActivateAccount,
            "player@example.test",
            passwordHash: null,
            cancellationToken: CancellationToken.None);
        var second = await client.WriteCommandAsync(
            AccountCommandType.ActivateAccount,
            "player@example.test",
            passwordHash: null,
            cancellationToken: CancellationToken.None);

        Assert.NotEqual(Guid.Empty, first.RequestId);
        Assert.NotEqual(first.RequestId, second.RequestId);
    }

    [Fact]
    public async Task Existing_request_collision_is_rejected_without_overwriting()
    {
        var client = CreateClient();
        var id = Guid.NewGuid();
        var first = await client.WriteCommandAsync(
            id,
            AccountCommandType.CreateAccount,
            "first@example.test",
            new byte[36],
            CancellationToken.None);
        var path = Path.Combine(_root, "incoming", $"{id:D}.json");
        var original = await File.ReadAllBytesAsync(path, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.WriteCommandAsync(
            id,
            AccountCommandType.CreateAccount,
            "second@example.test",
            new byte[36],
            CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(path, CancellationToken.None));
        Assert.Equal("first@example.test", first.Command.Email);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Existing_request_with_same_payload_is_reconciled_as_published()
    {
        var client = CreateClient();
        var id = Guid.NewGuid();
        var passwordHash = new byte[36];
        var first = await client.WriteCommandAsync(
            id,
            AccountCommandType.CreateAccount,
            "player@example.test",
            passwordHash,
            CancellationToken.None);

        var second = await client.WriteCommandAsync(
            id,
            AccountCommandType.CreateAccount,
            "PLAYER@example.test",
            passwordHash,
            CancellationToken.None);

        Assert.Equal(AccountQueuePublishOutcome.Published, second.Outcome);
        Assert.Equal(first.RequestId, second.RequestId);
        Assert.Equal(first.Command.Id, second.Command.Id);
        Assert.Equal(first.Command.Type, second.Command.Type);
        Assert.Equal(first.Command.Email, second.Command.Email);
        Assert.Equal(first.Command.PasswordHashBase64, second.Command.PasswordHashBase64);
        Assert.Equal(first.Command.SignatureBase64, second.Command.SignatureBase64);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "incoming"), "*.json"));
    }

    [Fact]
    public async Task Stale_fixed_temporary_file_does_not_block_idempotent_publication()
    {
        var client = CreateClient();
        var id = Guid.NewGuid();
        var staleTemporaryPath = Path.Combine(_root, "incoming", $"{id:D}.json.tmp");
        await File.WriteAllTextAsync(staleTemporaryPath, "stale temporary artifact", CancellationToken.None);

        var publication = await client.WriteCommandAsync(
            id,
            AccountCommandType.ActivateAccount,
            "player@example.test",
            passwordHash: null,
            CancellationToken.None);

        Assert.Equal(AccountQueuePublishOutcome.Published, publication.Outcome);
        Assert.True(File.Exists(Path.Combine(_root, "incoming", $"{id:D}.json")));
        Assert.Equal("stale temporary artifact", await File.ReadAllTextAsync(staleTemporaryPath));
    }

    [Fact]
    public async Task Rename_then_directory_sync_failure_returns_uncertain_with_stable_correlation()
    {
        var requestId = Guid.NewGuid();
        var registration = CreateQueuedRegistration();
        registration.QueueRequestId = requestId;
        var operations = new AccountQueueClientOperations
        {
            SyncDirectory = _ => throw new IOException("forced directory fsync failure")
        };
        var client = CreateClient(operations);

        var publication = await client.WriteCommandAsync(
            requestId,
            AccountCommandType.CreateAccount,
            registration.NormalizedEmail,
            registration.PasswordHash,
            CancellationToken.None);

        Assert.Equal(AccountQueuePublishOutcome.Uncertain, publication.Outcome);
        Assert.Equal(requestId, publication.RequestId);
        Assert.Equal(requestId, publication.Command.Id);
        Assert.Equal(RegistrationStatus.QueuePending, registration.Status);
        Assert.Equal(requestId, registration.QueueRequestId);
        var path = Path.Combine(_root, "incoming", $"{requestId:D}.json");
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        var persisted = JsonSerializer.Deserialize<AccountCommand>(await File.ReadAllTextAsync(path));
        Assert.NotNull(persisted);
        Assert.True(AccountCommandSigner.Verify(persisted, KeyBase64));
        var original = await File.ReadAllBytesAsync(path);

        var reconciled = await client.WriteCommandAsync(
            requestId,
            AccountCommandType.CreateAccount,
            registration.NormalizedEmail,
            registration.PasswordHash,
            CancellationToken.None);
        Assert.Equal(AccountQueuePublishOutcome.Published, reconciled.Outcome);
        Assert.Equal(requestId, reconciled.Command.Id);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task Failure_before_rename_leaves_no_final_and_cleans_owned_temporary_file()
    {
        var requestId = Guid.NewGuid();
        var client = CreateClient(new AccountQueueClientOperations
        {
            BeforeCommandRename = () => throw new IOException("forced pre-rename failure")
        });

        await Assert.ThrowsAsync<IOException>(() => client.WriteCommandAsync(
            requestId,
            AccountCommandType.CreateAccount,
            "player@example.test",
            new byte[36],
            CancellationToken.None));

        var finalPath = Path.Combine(_root, "incoming", $"{requestId:D}.json");
        Assert.False(File.Exists(finalPath));
        Assert.False(File.Exists(finalPath + ".tmp"));
    }

    [Fact]
    public void Non_linux_platform_cannot_construct_or_reach_the_publication_path()
    {
        var options = CreateQueueOptions();
        options.RootPath = Path.Combine(_root, "secret-queue-location");
        var directorySyncAttempted = false;
        var operations = new AccountQueueClientOperations
        {
            IsLinux = static () => false,
            SyncDirectory = _ => directorySyncAttempted = true
        };

        var exception = Assert.Throws<PlatformNotSupportedException>(() =>
            new AccountQueueClient(
                Microsoft.Extensions.Options.Options.Create(options),
                new FixedTimeProvider(Now),
                operations));

        Assert.Equal("The account queue integration is supported only on Linux.", exception.Message);
        Assert.DoesNotContain(options.RootPath, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyBase64, exception.Message, StringComparison.Ordinal);
        Assert.False(directorySyncAttempted);
        Assert.False(Path.Exists(options.RootPath));
    }

    [Fact]
    public void Queue_startup_validation_rejects_non_linux_without_exposing_configuration()
    {
        var options = CreateQueueOptions();
        options.RootPath = Path.Combine(_root, "secret-queue-location");

        var result = new QueueOptionsValidator(static () => false)
            .Validate(Microsoft.Extensions.Options.Options.DefaultName, options);

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("The account queue integration is supported only on Linux.", failure);
        Assert.DoesNotContain(options.RootPath, failure, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyBase64, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Queue_root_that_is_a_file_is_rejected()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_root)!);
        File.WriteAllText(_root, "not a directory");

        Assert.Throws<InvalidDataException>(() => CreateClient());
    }

    [Fact]
    public void Symlink_queue_root_is_rejected()
    {
        if (!OperatingSystem.IsLinux()) return;
        var target = _root + "-target";
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(_root, target);

        Assert.Throws<InvalidDataException>(() => CreateClient());
    }

    [Fact]
    public void Definitely_absent_requires_every_supported_directory_to_be_safely_inspected()
    {
        var client = CreateClient();
        foreach (var name in new[] { "processing", "processed", "rejected", "uncertain", "duplicates" })
        {
            Directory.CreateDirectory(Path.Combine(_root, name));
        }

        Assert.True(client.IsRequestDefinitelyAbsent(Guid.NewGuid()));
    }

    [Theory]
    [InlineData("incoming")]
    [InlineData("processing")]
    [InlineData("results")]
    [InlineData("processed")]
    [InlineData("rejected")]
    [InlineData("uncertain")]
    [InlineData("duplicates")]
    public void Artifact_in_any_supported_directory_is_not_definitely_absent(string directoryName)
    {
        var client = CreateClient();
        var requestId = Guid.NewGuid();
        var directory = Path.Combine(_root, directoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{requestId:D}.json"), "artifact");

        Assert.False(client.IsRequestDefinitelyAbsent(requestId));
    }

    [Fact]
    public void Collision_archives_with_unique_suffix_are_not_definitely_absent()
    {
        var client = CreateClient();
        var requestId = Guid.NewGuid();
        var duplicates = Path.Combine(_root, "duplicates");
        Directory.CreateDirectory(duplicates);
        File.WriteAllText(
            Path.Combine(duplicates, $"{requestId:D}.duplicate-{Guid.NewGuid():N}.json"),
            "artifact");

        Assert.False(client.IsRequestDefinitelyAbsent(requestId));
    }

    [Fact]
    public void Symlink_or_nondirectory_substitution_is_not_treated_as_absence()
    {
        if (!OperatingSystem.IsLinux()) return;
        var client = CreateClient();
        var processing = Path.Combine(_root, "processing");
        var target = Path.Combine(_root, "processing-target");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(processing, target);
        Assert.False(client.IsRequestDefinitelyAbsent(Guid.NewGuid()));
        Directory.Delete(processing);
        File.WriteAllText(processing, "not a directory");

        Assert.False(client.IsRequestDefinitelyAbsent(Guid.NewGuid()));
    }

    [Fact]
    public void Symlink_or_unsupported_request_artifact_is_not_treated_as_absence()
    {
        if (!OperatingSystem.IsLinux()) return;
        var client = CreateClient();
        var requestId = Guid.NewGuid();
        var processing = Path.Combine(_root, "processing");
        Directory.CreateDirectory(processing);
        var target = Path.Combine(_root, "outside-artifact");
        File.WriteAllText(target, "outside");
        var artifact = Path.Combine(processing, $"{requestId:D}.json");
        File.CreateSymbolicLink(artifact, target);
        Assert.False(client.IsRequestDefinitelyAbsent(requestId));
        File.Delete(artifact);
        Assert.Equal(0, CreateFifo(artifact, Convert.ToUInt32("600", 8)));

        Assert.False(client.IsRequestDefinitelyAbsent(requestId));
    }

    [Fact]
    public void Inaccessible_or_unknown_directory_inspection_is_not_treated_as_absence()
    {
        var client = CreateClient(new AccountQueueClientOperations
        {
            BeforeAbsentDirectoryOpen = name =>
            {
                if (name == "processing") throw new UnauthorizedAccessException("forced inaccessible directory");
            }
        });

        Assert.False(client.IsRequestDefinitelyAbsent(Guid.NewGuid()));
    }

    [Fact]
    public void Directory_replacement_race_is_not_treated_as_absence()
    {
        if (!OperatingSystem.IsLinux()) return;
        var processing = Path.Combine(_root, "processing");
        Directory.CreateDirectory(processing);
        var raced = false;
        var client = CreateClient(new AccountQueueClientOperations
        {
            AfterAbsentDirectoryOpen = name =>
            {
                if (name != "processing" || raced) return;
                raced = true;
                Directory.Move(processing, processing + "-original");
                Directory.CreateDirectory(processing);
            }
        });

        Assert.False(client.IsRequestDefinitelyAbsent(Guid.NewGuid()));
    }

    [Fact]
    public async Task ReadResult_is_pending_when_absent_and_does_not_delete_when_present()
    {
        var client = CreateClient();
        var id = Guid.NewGuid();

        var pending = await client.ReadResultAsync(
            id,
            "player@example.test",
            CancellationToken.None);
        Assert.Equal(AccountQueueReadStatus.Pending, pending.Status);

        var resultPath = WriteResult(new AccountCommandResult(
            id,
            AccountCommandStatus.Success,
            "created",
            "PLAYER@example.test",
            Now));
        var ready = await client.ReadResultAsync(
            id,
            "player@example.test",
            CancellationToken.None);

        Assert.Equal(AccountQueueReadStatus.Ready, ready.Status);
        Assert.NotNull(ready.Result);
        Assert.Equal("player@example.test", ready.Result.Email);
        Assert.True(File.Exists(resultPath));
    }

    [Fact]
    public async Task ReadResult_rejects_direct_symlink_without_following_it()
    {
        if (!OperatingSystem.IsLinux()) return;
        var id = Guid.NewGuid();
        var target = Path.Combine(_root, "outside-result.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(target, JsonSerializer.Serialize(new AccountCommandResult(
            id,
            AccountCommandStatus.Success,
            "created",
            "player@example.test",
            Now)));
        var client = CreateClient();
        var path = Path.Combine(_root, "results", $"{id:D}.json");
        File.CreateSymbolicLink(path, target);

        var read = await client.ReadResultAsync(id, "player@example.test", CancellationToken.None);

        Assert.Equal(AccountQueueReadStatus.Invalid, read.Status);
    }

    [Fact]
    public async Task ReadResult_rejects_candidate_swapped_to_symlink_before_open()
    {
        if (!OperatingSystem.IsLinux()) return;
        var id = Guid.NewGuid();
        var client = CreateClient(new AccountQueueClientOperations
        {
            BeforeResultOpen = () =>
            {
                var path = Path.Combine(_root, "results", $"{id:D}.json");
                var target = Path.Combine(_root, "swapped-result.json");
                File.Move(path, path + ".original");
                File.WriteAllText(target, JsonSerializer.Serialize(new AccountCommandResult(
                    id,
                    AccountCommandStatus.Success,
                    "created",
                    "player@example.test",
                    Now)));
                File.CreateSymbolicLink(path, target);
            }
        });
        WriteResult(new AccountCommandResult(
            id,
            AccountCommandStatus.Success,
            "created",
            "player@example.test",
            Now));

        var read = await client.ReadResultAsync(id, "player@example.test", CancellationToken.None);

        Assert.Equal(AccountQueueReadStatus.Invalid, read.Status);
    }

    [Fact]
    public async Task ReadResult_holds_results_directory_handle_across_path_replacement()
    {
        if (!OperatingSystem.IsLinux()) return;
        var id = Guid.NewGuid();
        WriteResult(new AccountCommandResult(
            id,
            AccountCommandStatus.Success,
            "created",
            "player@example.test",
            Now));
        var originalResults = Path.Combine(_root, "results-original");
        var attackerResults = Path.Combine(_root, "attacker-results");
        var client = CreateClient(new AccountQueueClientOperations
        {
            BeforeResultOpen = () =>
            {
                var results = Path.Combine(_root, "results");
                Directory.Move(results, originalResults);
                Directory.CreateDirectory(attackerResults);
                File.WriteAllText(
                    Path.Combine(attackerResults, $"{id:D}.json"),
                    JsonSerializer.Serialize(new AccountCommandResult(
                        id,
                        AccountCommandStatus.Success,
                        "created",
                        "attacker@example.test",
                        Now)));
                Directory.CreateSymbolicLink(results, attackerResults);
            }
        });

        var read = await client.ReadResultAsync(id, "player@example.test", CancellationToken.None);

        Assert.Equal(AccountQueueReadStatus.Ready, read.Status);
        Assert.Equal("player@example.test", read.Result!.Email);
    }

    [Fact]
    public async Task ReadResult_rejects_fifo_without_blocking()
    {
        if (!OperatingSystem.IsLinux()) return;
        var id = Guid.NewGuid();
        var client = CreateClient();
        var path = Path.Combine(_root, "results", $"{id:D}.json");
        Assert.Equal(0, CreateFifo(path, Convert.ToUInt32("600", 8)));

        var readTask = client.ReadResultAsync(id, "player@example.test", CancellationToken.None);
        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(readTask, completed);
        Assert.Equal(AccountQueueReadStatus.Invalid, (await readTask).Status);
    }

    [Fact]
    public async Task ReadResult_rejects_growth_beyond_limit_from_same_open_handle()
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(_root, "results", $"{id:D}.json");
        var client = CreateClient(new AccountQueueClientOperations
        {
            AfterResultInitialLengthObserved = () =>
            {
                using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                writer.Write(new byte[(64 * 1024) + 1]);
            }
        });
        WriteResult(new AccountCommandResult(
            id,
            AccountCommandStatus.Success,
            "created",
            "player@example.test",
            Now));

        var read = await client.ReadResultAsync(id, "player@example.test", CancellationToken.None);

        Assert.Equal(AccountQueueReadStatus.Invalid, read.Status);
    }

    [Fact]
    public async Task ReadResult_accepts_and_closes_regular_file_opened_as_descriptor_zero()
    {
        if (!OperatingSystem.IsLinux()) return;

        var repositoryRoot = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var probePath = Path.Combine(
            repositoryRoot,
            "Mir3.Web.FdZeroProbe",
            "bin",
            configuration,
            "net10.0",
            "Mir3.Web.FdZeroProbe");
        Assert.True(File.Exists(probePath), $"fd-zero probe was not built at {probePath}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = probePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        Assert.True(process.Start());
        var standardOutput = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardError = await process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);

        Assert.True(
            process.ExitCode == 0,
            $"fd-zero probe failed with exit code {process.ExitCode}: {standardError}");
        Assert.Equal(
            "descriptor=0;status=Ready;closed=True;reused=True",
            standardOutput.Trim());
    }

    [Fact]
    public async Task ReadResult_rejects_directory_as_nonregular_file()
    {
        var id = Guid.NewGuid();
        var client = CreateClient();
        Directory.CreateDirectory(Path.Combine(_root, "results", $"{id:D}.json"));

        var read = await client.ReadResultAsync(id, "player@example.test", CancellationToken.None);

        Assert.Equal(AccountQueueReadStatus.Invalid, read.Status);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("mismatched-id")]
    [InlineData("mismatched-email")]
    [InlineData("null-email")]
    public async Task ReadResult_safely_rejects_malformed_or_mismatched_identity(string scenario)
    {
        var client = CreateClient();
        var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(_root, "results"));
        var path = Path.Combine(_root, "results", $"{id:D}.json");
        if (scenario == "malformed")
        {
            await File.WriteAllTextAsync(path, "{ secret malformed content", CancellationToken.None);
        }
        else
        {
            var result = new AccountCommandResult(
                scenario == "mismatched-id" ? Guid.NewGuid() : id,
                AccountCommandStatus.Success,
                "created",
                scenario switch
                {
                    "mismatched-email" => "other@example.test",
                    "null-email" => null!,
                    _ => "player@example.test"
                },
                Now);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result), CancellationToken.None);
        }

        var read = await client.ReadResultAsync(
            id,
            "player@example.test",
            CancellationToken.None);

        Assert.Equal(AccountQueueReadStatus.Invalid, read.Status);
        Assert.Null(read.Result);
        Assert.Equal("invalid-result", read.ErrorCode);
        Assert.True(File.Exists(path));
        Assert.DoesNotContain("secret", read.ErrorCode, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    public void Queue_options_reject_absent_or_malformed_hmac_key(string? key)
    {
        var options = new QueueOptions { RootPath = _root, HmacKeyBase64 = key };

        var result = new QueueOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, options);

        Assert.True(result.Failed);
        if (!string.IsNullOrEmpty(key))
        {
            Assert.DoesNotContain(key, string.Join(" ", result.Failures), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void Queue_options_reject_wrong_sized_hmac_key(int length)
    {
        var options = new QueueOptions
        {
            RootPath = _root,
            HmacKeyBase64 = Convert.ToBase64String(new byte[length])
        };

        var result = new QueueOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.DoesNotContain(options.HmacKeyBase64, string.Join(" ", result.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Queue_options_setup_reads_paths_from_config_but_key_only_from_environment()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Queue:RootPath"] = "/configured/root",
                ["Queue:ResultPollSeconds"] = "7",
                ["Queue:HmacKeyBase64"] = Convert.ToBase64String(new byte[32])
            })
            .Build();
        var setup = new QueueOptionsSetup(config, name =>
            name == QueueOptions.HmacEnvironmentVariable ? KeyBase64 : null);
        var options = new QueueOptions();

        setup.Configure(options);

        Assert.Equal("/configured/root", options.RootPath);
        Assert.Equal(7, options.ResultPollSeconds);
        Assert.Equal(KeyBase64, options.HmacKeyBase64);
    }

    [Fact]
    public async Task Result_worker_fairly_scans_beyond_the_first_batch_across_restarts_and_retries_results()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "fairness.db");
        var dbOptions = new DbContextOptionsBuilder<PortalDbContext>()
            .UseSqlite($"Data Source={databasePath};Default Timeout=5")
            .Options;
        var factory = new TestDbContextFactory(dbOptions);
        var registrations = Enumerable.Range(0, 51)
            .Select(index => CreateQueuedRegistration(
                $"player{index:D2}@example.test",
                Now.UtcDateTime.AddMinutes(-100).AddSeconds(index)))
            .ToArray();
        var originalPending = registrations.Take(50).ToDictionary(
            registration => registration.Id,
            registration => new
            {
                registration.QueueRequestId,
                registration.QueueCommandType,
                registration.UpdatedUtc
            });
        await using (var setup = await factory.CreateDbContextAsync(CancellationToken.None))
        {
            await setup.Database.MigrateAsync(CancellationToken.None);
            setup.Registrations.AddRange(registrations);
            await setup.SaveChangesAsync(CancellationToken.None);
        }

        var laterReady = registrations[50];
        WriteResult(new AccountCommandResult(
            laterReady.QueueRequestId!.Value,
            AccountCommandStatus.Success,
            "created",
            laterReady.NormalizedEmail,
            Now));

        await CreateWorker(factory).ProcessOnceAsync(CancellationToken.None);
        await using (var firstPassVerification = await factory.CreateDbContextAsync(CancellationToken.None))
        {
            var firstPass = await firstPassVerification.Registrations.ToListAsync(CancellationToken.None);
            Assert.All(
                firstPass.Where(item => item.Id != laterReady.Id),
                item =>
                {
                    var original = originalPending[item.Id];
                    Assert.Equal(RegistrationStatus.QueuePending, item.Status);
                    Assert.Equal(original.QueueRequestId, item.QueueRequestId);
                    Assert.Equal(original.QueueCommandType, item.QueueCommandType);
                    Assert.Null(item.QueueLastErrorCode);
                    Assert.Equal(original.UpdatedUtc, item.UpdatedUtc);
                    Assert.NotNull(item.QueueLastCheckedUtc);
                });
            var unexamined = firstPass.Single(item => item.Id == laterReady.Id);
            Assert.Equal(RegistrationStatus.QueuePending, unexamined.Status);
            Assert.Null(unexamined.QueueLastCheckedUtc);
            Assert.Empty(await firstPassVerification.AuditEntries.ToListAsync(CancellationToken.None));
        }

        await CreateWorker(factory).ProcessOnceAsync(CancellationToken.None);

        await using (var verification = await factory.CreateDbContextAsync(CancellationToken.None))
        {
            var saved = await verification.Registrations
                .OrderBy(registration => registration.UpdatedUtc)
                .ToListAsync(CancellationToken.None);
            Assert.Equal(RegistrationStatus.Active, saved.Single(item => item.Id == laterReady.Id).Status);
            Assert.All(
                saved.Where(item => item.Id != laterReady.Id),
                item =>
                {
                    var original = originalPending[item.Id];
                    Assert.Equal(RegistrationStatus.QueuePending, item.Status);
                    Assert.Equal(original.QueueRequestId, item.QueueRequestId);
                    Assert.Equal(original.QueueCommandType, item.QueueCommandType);
                    Assert.Null(item.QueueLastErrorCode);
                    Assert.Equal(original.UpdatedUtc, item.UpdatedUtc);
                });
            var audit = Assert.Single(await verification.AuditEntries.ToListAsync(CancellationToken.None));
            Assert.Contains(laterReady.Id.ToString("D"), audit.Target, StringComparison.Ordinal);
        }

        var appearingLater = registrations[49];
        WriteResult(new AccountCommandResult(
            appearingLater.QueueRequestId!.Value,
            AccountCommandStatus.Success,
            "created",
            appearingLater.NormalizedEmail,
            Now));
        var correctedLater = registrations[48];
        var correctedPath = Path.Combine(_root, "results", $"{correctedLater.QueueRequestId!.Value:D}.json");
        await File.WriteAllTextAsync(correctedPath, "{ malformed result", CancellationToken.None);

        await CreateWorker(factory).ProcessOnceAsync(CancellationToken.None);
        await using (var invalidVerification = await factory.CreateDbContextAsync(CancellationToken.None))
        {
            var invalid = await invalidVerification.Registrations.SingleAsync(
                item => item.Id == correctedLater.Id,
                CancellationToken.None);
            var original = originalPending[correctedLater.Id];
            Assert.Equal(RegistrationStatus.QueuePending, invalid.Status);
            Assert.Equal(original.QueueRequestId, invalid.QueueRequestId);
            Assert.Equal(original.QueueCommandType, invalid.QueueCommandType);
            Assert.Null(invalid.QueueLastErrorCode);
            Assert.Equal(original.UpdatedUtc, invalid.UpdatedUtc);
            Assert.Equal(2, await invalidVerification.AuditEntries.CountAsync(CancellationToken.None));
        }

        WriteResult(new AccountCommandResult(
            correctedLater.QueueRequestId.Value,
            AccountCommandStatus.Success,
            "created",
            correctedLater.NormalizedEmail,
            Now));
        for (var cycle = 0; cycle < 4; cycle++)
        {
            await CreateWorker(factory).ProcessOnceAsync(CancellationToken.None);
        }

        await using (var verification = await factory.CreateDbContextAsync(CancellationToken.None))
        {
            Assert.Equal(
                RegistrationStatus.Active,
                (await verification.Registrations.SingleAsync(
                    item => item.Id == appearingLater.Id,
                    CancellationToken.None)).Status);
            Assert.Equal(
                RegistrationStatus.Active,
                (await verification.Registrations.SingleAsync(
                    item => item.Id == correctedLater.Id,
                    CancellationToken.None)).Status);
            Assert.Equal(3, await verification.AuditEntries.CountAsync(CancellationToken.None));
        }

        await Task.WhenAll(
            CreateWorker(factory).ProcessOnceAsync(CancellationToken.None),
            CreateWorker(factory).ProcessOnceAsync(CancellationToken.None));
        await using var finalVerification = await factory.CreateDbContextAsync(CancellationToken.None);
        Assert.Equal(3, await finalVerification.AuditEntries.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Result_worker_applies_once_writes_one_audit_and_keeps_result_file()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "portal.db");
        var dbOptions = new DbContextOptionsBuilder<PortalDbContext>()
            .UseSqlite($"Data Source={databasePath};Default Timeout=5")
            .Options;
        var factory = new TestDbContextFactory(dbOptions);
        var registration = CreateQueuedRegistration();
        var requestId = registration.QueueRequestId!.Value;
        await using (var setup = await factory.CreateDbContextAsync(CancellationToken.None))
        {
            await setup.Database.MigrateAsync(CancellationToken.None);
            setup.Registrations.Add(registration);
            await setup.SaveChangesAsync(CancellationToken.None);
        }

        var client = CreateClient();
        var resultPath = WriteResult(new AccountCommandResult(
            registration.QueueRequestId!.Value,
            AccountCommandStatus.Success,
            "created",
            registration.NormalizedEmail,
            Now));
        var worker = new AccountQueueResultWorker(
            factory,
            client,
            new RegistrationStateMachine(),
            Microsoft.Extensions.Options.Options.Create(CreateQueueOptions()),
            NullLogger<AccountQueueResultWorker>.Instance,
            new FixedTimeProvider(Now));

        await Task.WhenAll(
            worker.ProcessOnceAsync(CancellationToken.None),
            worker.ProcessOnceAsync(CancellationToken.None));
        await worker.ProcessOnceAsync(CancellationToken.None);

        await using var verification = await factory.CreateDbContextAsync(CancellationToken.None);
        var saved = await verification.Registrations.SingleAsync(CancellationToken.None);
        Assert.Equal(RegistrationStatus.Active, saved.Status);
        Assert.Equal(Now.UtcDateTime, saved.GameActivatedUtc);
        var audit = Assert.Single(await verification.AuditEntries.ToListAsync(CancellationToken.None));
        Assert.Equal("account-queue", audit.Actor);
        Assert.Equal("registration.queue-result", audit.Action);
        Assert.Contains(requestId.ToString("D"), audit.DetailsJson, StringComparison.Ordinal);
        Assert.True(File.Exists(resultPath));
    }

    [Theory]
    [InlineData(AccountCommandType.ActivateAccount)]
    [InlineData(AccountCommandType.DeactivateAccount)]
    public async Task Result_worker_terminally_applies_missing_account_once_without_retry(
        AccountCommandType commandType)
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, $"not-found-{commandType}.db");
        var dbOptions = new DbContextOptionsBuilder<PortalDbContext>()
            .UseSqlite($"Data Source={databasePath};Default Timeout=5")
            .Options;
        var factory = new TestDbContextFactory(dbOptions);
        var registration = CreateQueuedRegistration();
        registration.QueueCommandType = commandType;
        var requestId = registration.QueueRequestId!.Value;
        await using (var setup = await factory.CreateDbContextAsync(CancellationToken.None))
        {
            await setup.Database.MigrateAsync(CancellationToken.None);
            setup.Registrations.Add(registration);
            await setup.SaveChangesAsync(CancellationToken.None);
        }

        WriteResult(new AccountCommandResult(
            requestId,
            AccountCommandStatus.NotFound,
            "account-not-found",
            registration.NormalizedEmail,
            Now));
        var worker = CreateWorker(factory);

        await worker.ProcessOnceAsync(CancellationToken.None);
        await worker.ProcessOnceAsync(CancellationToken.None);

        await using var verification = await factory.CreateDbContextAsync(CancellationToken.None);
        var saved = await verification.Registrations.SingleAsync(CancellationToken.None);
        Assert.Equal(RegistrationStatus.Failed, saved.Status);
        Assert.Equal("account-not-found", saved.QueueLastErrorCode);
        Assert.Null(saved.QueueRequestId);
        Assert.Null(saved.QueueCommandType);
        Assert.Null(saved.QueueLastCheckedUtc);
        var audit = Assert.Single(await verification.AuditEntries.ToListAsync(CancellationToken.None));
        Assert.Contains(requestId.ToString("D"), audit.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("account-not-found", audit.DetailsJson, StringComparison.Ordinal);
    }

    private AccountQueueClient CreateClient() =>
        CreateClient(AccountQueueClientOperations.Default);

    private AccountQueueClient CreateClient(AccountQueueClientOperations operations) =>
        new(
            Microsoft.Extensions.Options.Options.Create(CreateQueueOptions()),
            new FixedTimeProvider(Now),
            operations);

    private QueueOptions CreateQueueOptions() => new()
    {
        RootPath = _root,
        ResultPollSeconds = 2,
        HmacKeyBase64 = KeyBase64
    };

    private AccountQueueResultWorker CreateWorker(IDbContextFactory<PortalDbContext> factory) =>
        new(
            factory,
            CreateClient(),
            new RegistrationStateMachine(),
            Microsoft.Extensions.Options.Options.Create(CreateQueueOptions()),
            NullLogger<AccountQueueResultWorker>.Instance,
            new FixedTimeProvider(Now));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Zircon Server.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for the fd-zero probe.");
    }

    private string WriteResult(AccountCommandResult result)
    {
        var results = Path.Combine(_root, "results");
        Directory.CreateDirectory(results);
        var path = Path.Combine(results, $"{result.Id:D}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(result));
        return path;
    }

    private static Registration CreateQueuedRegistration() =>
        CreateQueuedRegistration("player@example.test", Now.UtcDateTime.AddMinutes(-1));

    private static Registration CreateQueuedRegistration(string normalizedEmail, DateTime updatedUtc) => new()
    {
        Id = Guid.NewGuid(),
        Email = normalizedEmail,
        NormalizedEmail = normalizedEmail,
        PasswordHash = new byte[36],
        Status = RegistrationStatus.QueuePending,
        VerificationTokenHash = new byte[32],
        VerificationExpiresUtc = Now.UtcDateTime.AddHours(1),
        EmailVerifiedUtc = Now.UtcDateTime.AddMinutes(-1),
        QueueRequestId = Guid.NewGuid(),
        QueueCommandType = AccountCommandType.CreateAccount,
        CreatedUtc = Now.UtcDateTime.AddHours(-1),
        UpdatedUtc = updatedUtc,
        SourceIpHash = new byte[32]
    };

    public void Dispose()
    {
        if (Directory.Exists(_root) && (File.GetAttributes(_root) & FileAttributes.ReparsePoint) == 0)
        {
            Directory.Delete(_root, recursive: true);
        }
        else if (File.Exists(_root) || Directory.Exists(_root))
        {
            File.Delete(_root);
        }

        var target = _root + "-target";
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestDbContextFactory(DbContextOptions<PortalDbContext> options)
        : IDbContextFactory<PortalDbContext>
    {
        public PortalDbContext CreateDbContext() => new(options);

        public Task<PortalDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int CreateFifo(string path, uint mode);
}
