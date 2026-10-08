using AccountPortal.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mir3.Web.Data;
using Mir3.Web.Domain;
using Mir3.Web.Options;
using Mir3.Web.Security;
using Mir3.Web.Services;

namespace Mir3.Web.Tests;

public sealed class AdminPortalServiceTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] HmacKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mir3-admin-tests-{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly TestDbContextFactory _factory;
    private readonly VerificationTransportLockManager _transportLocks;
    private readonly EmailVerificationService _emailVerification;
    private readonly AccountQueueClient _queue;
    private readonly AdminPortalService _service;
    private readonly GamePasswordHasher _gamePasswordHasher = new();

    public AdminPortalServiceTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "portal.db");
        var options = new DbContextOptionsBuilder<PortalDbContext>()
            .UseSqlite($"Data Source={_databasePath};Default Timeout=10")
            .Options;
        _factory = new TestDbContextFactory(options);
        using (var setup = _factory.CreateDbContext())
        {
            setup.Database.Migrate();
        }

        _transportLocks = new VerificationTransportLockManager(
            $"Data Source={_databasePath}",
            Path.Combine(_root, "verification-locks"));
        _emailVerification = new EmailVerificationService(
            _factory,
            _transportLocks,
            new VerificationTokenService(new FixedTimeProvider(Now)),
            new NoopEmailSender(),
            Microsoft.Extensions.Options.Options.Create(new SmtpOptions { PublicBaseUrl = "https://portal.example.invalid", Username = "test-user", Password = "test-password" }),
            NullLogger<EmailVerificationService>.Instance,
            new FixedTimeProvider(Now));
        _queue = new AccountQueueClient(
            Microsoft.Extensions.Options.Options.Create(new QueueOptions
            {
                RootPath = Path.Combine(_root, "account-queue"),
                ResultPollSeconds = 1,
                HmacKeyBase64 = Convert.ToBase64String(HmacKey)
            }),
            new FixedTimeProvider(Now));
        var outbox = new RegistrationOutboxProcessor(
            _factory,
            _emailVerification,
            _queue,
            NullLogger<RegistrationOutboxProcessor>.Instance);
        _service = new AdminPortalService(
            _factory,
            new PasswordHasher<AdminUser>(),
            new RegistrationStateMachine(),
            outbox,
            _gamePasswordHasher,
            new FixedTimeProvider(Now),
            NullLogger<AdminPortalService>.Instance);
    }

    [Theory]
    [InlineData(true, RegistrationStatus.Disabled, AccountCommandType.ActivateAccount)]
    [InlineData(false, RegistrationStatus.Active, AccountCommandType.DeactivateAccount)]
    public async Task Concurrent_same_registration_actions_have_one_winner_and_one_audit(
        bool activate,
        RegistrationStatus initialStatus,
        AccountCommandType expectedCommand)
    {
        var registration = CreateRegistration(initialStatus);
        await using (var setup = await _factory.CreateDbContextAsync())
        {
            setup.Registrations.Add(registration);
            await setup.SaveChangesAsync();
        }

        var results = await Task.WhenAll(
            _service.ApplyRegistrationActionAsync(registration.Id, activate, "admin:first"),
            _service.ApplyRegistrationActionAsync(registration.Id, activate, "admin:second"));

        Assert.Single(results, result => result.Succeeded);
        var loser = Assert.Single(results, result => !result.Succeeded);
        Assert.Contains("current state", loser.Message, StringComparison.Ordinal);

        await using var verification = await _factory.CreateDbContextAsync();
        var saved = await verification.Registrations.SingleAsync();
        Assert.Equal(RegistrationStatus.QueuePending, saved.Status);
        Assert.Equal(expectedCommand, saved.QueueCommandType);
        Assert.NotNull(saved.QueueRequestId);
        Assert.Equal(QueuePublicationState.Published, saved.QueuePublicationState);
        Assert.Single(await verification.AuditEntries.ToListAsync());

        var commandPath = Path.Combine(
            _root,
            "account-queue",
            "incoming",
            $"{saved.QueueRequestId!.Value:D}.json");
        Assert.True(File.Exists(commandPath));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "account-queue", "incoming"), "*.json"));
    }

    [Fact]
    public async Task Outbox_reconciles_a_final_command_left_before_database_publication()
    {
        var registration = CreateQueuedRegistration();
        await using (var setup = await _factory.CreateDbContextAsync())
        {
            setup.Registrations.Add(registration);
            await setup.SaveChangesAsync();
        }

        var publication = await _queue.WriteCommandAsync(
            registration.QueueRequestId!.Value,
            registration.QueueCommandType!.Value,
            registration.NormalizedEmail,
            registration.PasswordHash,
            CancellationToken.None);
        Assert.Equal(AccountQueuePublishOutcome.Published, publication.Outcome);

        var processor = new RegistrationOutboxProcessor(
            _factory,
            _emailVerification,
            _queue,
            NullLogger<RegistrationOutboxProcessor>.Instance);
        await processor.ProcessQueueOnceAsync(CancellationToken.None);

        await using var verification = await _factory.CreateDbContextAsync();
        var saved = await verification.Registrations.SingleAsync();
        Assert.Equal(QueuePublicationState.Published, saved.QueuePublicationState);
        Assert.Equal(registration.QueueRequestId, saved.QueueRequestId);
    }

    [Fact]
    public async Task Admin_can_queue_password_reset_without_persisting_plaintext_password()
    {
        var registration = CreateRegistration(RegistrationStatus.Active);
        await using (var setup = await _factory.CreateDbContextAsync())
        {
            setup.Registrations.Add(registration);
            await setup.SaveChangesAsync();
        }

        const string replacementPassword = "NewPass9";
        var result = await _service.ResetPasswordAsync(
            registration.Id,
            replacementPassword,
            "admin:reset",
            CancellationToken.None);

        Assert.True(result.Succeeded);
        await using var verification = await _factory.CreateDbContextAsync();
        var saved = await verification.Registrations.SingleAsync();
        Assert.Equal(RegistrationStatus.QueuePending, saved.Status);
        Assert.Equal(AccountCommandType.ResetPassword, saved.QueueCommandType);
        Assert.NotNull(saved.PendingPasswordHash);
        Assert.Equal(36, saved.PendingPasswordHash!.Length);
        Assert.NotEqual(replacementPassword, Convert.ToBase64String(saved.PendingPasswordHash));
        var commandPath = Path.Combine(
            _root,
            "account-queue",
            "incoming",
            $"{saved.QueueRequestId!.Value:D}.json");
        var command = System.Text.Json.JsonSerializer.Deserialize<AccountPortal.Contracts.AccountCommand>(
            await File.ReadAllTextAsync(commandPath));
        Assert.NotNull(command);
        Assert.Equal(Convert.ToBase64String(saved.PendingPasswordHash), command!.PasswordHashBase64);
        Assert.Single(await verification.AuditEntries
            .Where(item => item.Action == "Admin.Registration.ResetPassword")
            .ToListAsync());
    }

    private static Registration CreateRegistration(RegistrationStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Email = "player@example.test",
        NormalizedEmail = "player@example.test",
        PasswordHash = new byte[36],
        Status = status,
        VerificationTokenHash = new byte[32],
        VerificationExpiresUtc = Now.UtcDateTime.AddHours(1),
        EmailVerifiedUtc = Now.UtcDateTime.AddMinutes(-1),
        CreatedUtc = Now.UtcDateTime.AddHours(-1),
        UpdatedUtc = Now.UtcDateTime.AddMinutes(-1),
        SourceIpHash = new byte[32]
    };

    private static Registration CreateQueuedRegistration() => new()
    {
        Id = Guid.NewGuid(),
        Email = "queued@example.test",
        NormalizedEmail = "queued@example.test",
        PasswordHash = new byte[36],
        Status = RegistrationStatus.QueuePending,
        VerificationTokenHash = new byte[32],
        VerificationExpiresUtc = Now.UtcDateTime.AddHours(1),
        EmailVerifiedUtc = Now.UtcDateTime.AddMinutes(-1),
        QueueRequestId = Guid.NewGuid(),
        QueuePublicationState = QueuePublicationState.Pending,
        QueueCommandType = AccountCommandType.CreateAccount,
        CreatedUtc = Now.UtcDateTime.AddHours(-1),
        UpdatedUtc = Now.UtcDateTime.AddMinutes(-1),
        SourceIpHash = new byte[32]
    };

    public async ValueTask DisposeAsync()
    {
        _transportLocks.Dispose();
        await Task.Yield();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class NoopEmailSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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
}
