using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mir3.Web.Data;
using Mir3.Web.Domain;

namespace Mir3.Web.Tests;

public sealed class PortalPersistenceTests
{
    [Fact]
    public void Registration_status_values_are_exact()
    {
        Assert.Equal(
            [
                RegistrationStatus.PendingEmail,
                RegistrationStatus.AwaitingAdmin,
                RegistrationStatus.QueuePending,
                RegistrationStatus.Active,
                RegistrationStatus.Disabled,
                RegistrationStatus.Failed
            ],
            Enum.GetValues<RegistrationStatus>());
    }

    [Fact]
    public async Task Normalized_email_is_unique_in_sqlite()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.Registrations.Add(CreateRegistration("first@example.test", "USER@EXAMPLE.TEST"));
        database.Context.Registrations.Add(CreateRegistration("second@example.test", "USER@EXAMPLE.TEST"));

        await Assert.ThrowsAsync<DbUpdateException>(() => database.Context.SaveChangesAsync());
    }

    [Theory]
    [InlineData("password", 35)]
    [InlineData("password", 37)]
    [InlineData("token", 31)]
    [InlineData("token", 33)]
    [InlineData("ip", 31)]
    [InlineData("ip", 33)]
    public async Task Hash_lengths_are_enforced_by_sqlite(string hash, int length)
    {
        await using var database = await TestDatabase.CreateAsync();
        var registration = CreateRegistration("person@example.test", "PERSON@EXAMPLE.TEST");
        SetHash(registration, hash, new byte[length]);
        database.Context.Registrations.Add(registration);

        await Assert.ThrowsAsync<DbUpdateException>(() => database.Context.SaveChangesAsync());
    }

    [Theory]
    [InlineData("password")]
    [InlineData("token")]
    [InlineData("ip")]
    public async Task Hashes_are_required_by_sqlite(string hash)
    {
        await using var database = await TestDatabase.CreateAsync();
        var registration = CreateRegistration("person@example.test", "PERSON@EXAMPLE.TEST");
        SetHash(registration, hash, null!);
        database.Context.Registrations.Add(registration);

        await Assert.ThrowsAsync<DbUpdateException>(() => database.Context.SaveChangesAsync());
    }

    [Theory]
    [InlineData("registration-email", 320)]
    [InlineData("admin-password-hash", 512)]
    [InlineData("audit-details", 4096)]
    public async Task Bounded_text_lengths_are_enforced_by_migrated_sqlite_database(
        string field,
        int maximumLength)
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var overlongValue = new string('x', maximumLength + 1);
        var now = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

        switch (field)
        {
            case "registration-email":
                var registration = CreateRegistration("person@example.test", "PERSON@EXAMPLE.TEST");
                registration.Email = overlongValue;
                database.Context.Registrations.Add(registration);
                break;
            case "admin-password-hash":
                database.Context.AdminUsers.Add(new AdminUser
                {
                    Id = Guid.NewGuid(),
                    Username = "admin",
                    NormalizedUsername = "ADMIN",
                    PasswordHash = overlongValue,
                    CreatedUtc = now,
                    UpdatedUtc = now
                });
                break;
            case "audit-details":
                database.Context.AuditEntries.Add(new AuditEntry
                {
                    Actor = "admin:first",
                    Action = "registration.view",
                    Target = "registration:first",
                    DetailsJson = overlongValue,
                    CreatedUtc = now
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field));
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => database.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task Initializer_inserts_one_default_portal_setting()
    {
        await using var database = await TestDatabase.CreateAsync();

        await PortalDbInitializer.InitializeAsync(database.Context, null, null);
        await PortalDbInitializer.InitializeAsync(database.Context, null, null);

        var setting = Assert.Single(await database.Context.PortalSettings.ToListAsync());
        Assert.Equal(PortalSetting.SingletonId, setting.Id);
        Assert.Equal(PortalSetting.SingletonKey, setting.Key);
        Assert.False(setting.AutoActivateAfterEmailVerification);
        Assert.NotEqual(default, setting.UpdatedUtc);
    }

    [Theory]
    [InlineData(2, "portal")]
    [InlineData(1, "other")]
    public async Task Portal_setting_singleton_id_and_key_are_enforced_by_sqlite(int id, string key)
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.PortalSettings.Add(new PortalSetting
        {
            Id = id,
            Key = key,
            UpdatedUtc = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc)
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => database.Context.SaveChangesAsync());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("admin", null)]
    [InlineData(null, "already-hashed")]
    [InlineData("", "already-hashed")]
    [InlineData("admin", "")]
    public async Task Initializer_does_not_create_admin_without_both_bootstrap_values(
        string? username,
        string? passwordHash)
    {
        await using var database = await TestDatabase.CreateAsync();

        await PortalDbInitializer.InitializeAsync(database.Context, username, passwordHash);

        Assert.Empty(await database.Context.AdminUsers.ToListAsync());
    }

    [Fact]
    public async Task Initializer_creates_first_admin_once_without_replacing_it()
    {
        await using var database = await TestDatabase.CreateAsync();

        await PortalDbInitializer.InitializeAsync(database.Context, " FirstAdmin ", "already-hashed-1");
        await PortalDbInitializer.InitializeAsync(database.Context, "Replacement", "already-hashed-2");

        var admin = Assert.Single(await database.Context.AdminUsers.ToListAsync());
        Assert.Equal("FirstAdmin", admin.Username);
        Assert.Equal("FIRSTADMIN", admin.NormalizedUsername);
        Assert.Equal("already-hashed-1", admin.PasswordHash);
        Assert.Equal(0, admin.FailedAttempts);
        Assert.Null(admin.LockoutUntilUtc);
        Assert.NotEqual(default, admin.CreatedUtc);
        Assert.Equal(admin.CreatedUtc, admin.UpdatedUtc);
    }

    [Fact]
    public async Task Initializer_can_retry_with_same_context_after_post_staging_failure()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var overlongPasswordHash = new string('x', 513);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            PortalDbInitializer.InitializeAsync(database.Context, "FirstAdmin", overlongPasswordHash));

        await using (var verificationContext = new PortalDbContext(TestDatabase.CreateOptions(database.Path)))
        {
            Assert.Empty(await verificationContext.PortalSettings.ToListAsync());
            Assert.Empty(await verificationContext.AdminUsers.ToListAsync());
        }

        await PortalDbInitializer.InitializeAsync(database.Context, "FirstAdmin", "already-hashed");

        await using var finalContext = new PortalDbContext(TestDatabase.CreateOptions(database.Path));
        Assert.Single(await finalContext.PortalSettings.ToListAsync());
        Assert.Single(await finalContext.AdminUsers.ToListAsync());
    }

    [Fact]
    public async Task Initializer_restores_tracked_state_when_cancelled_after_save()
    {
        using var cancellation = new CancellationTokenSource();
        await using var database = await TestDatabase.CreateMigratedAsync(
            new CancelAfterSaveInterceptor(cancellation));
        var registration = CreateRegistration("person@example.test", "PERSON@EXAMPLE.TEST");
        var auditEntry = new AuditEntry
        {
            Actor = "admin:first",
            Action = "initializer.test",
            Target = "portal",
            CreatedUtc = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc)
        };
        database.Context.Registrations.Add(registration);
        database.Context.AuditEntries.Add(auditEntry);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PortalDbInitializer.InitializeAsync(
                database.Context,
                "FirstAdmin",
                "already-hashed",
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(EntityState.Added, database.Context.Entry(registration).State);
        Assert.Equal(EntityState.Added, database.Context.Entry(auditEntry).State);
        Assert.Equal(0, auditEntry.Id);
        await using (var verificationContext = new PortalDbContext(TestDatabase.CreateOptions(database.Path)))
        {
            Assert.Empty(await verificationContext.Registrations.ToListAsync());
            Assert.Empty(await verificationContext.PortalSettings.ToListAsync());
            Assert.Empty(await verificationContext.AdminUsers.ToListAsync());
            Assert.Empty(await verificationContext.AuditEntries.ToListAsync());
        }

        await PortalDbInitializer.InitializeAsync(database.Context, "FirstAdmin", "already-hashed");

        await using var finalContext = new PortalDbContext(TestDatabase.CreateOptions(database.Path));
        Assert.Single(await finalContext.Registrations.ToListAsync());
        Assert.Single(await finalContext.PortalSettings.ToListAsync());
        Assert.Single(await finalContext.AdminUsers.ToListAsync());
        Assert.Single(await finalContext.AuditEntries.ToListAsync());
    }

    [Fact]
    public async Task Initializer_is_safe_across_concurrent_contexts_for_the_same_sqlite_file()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var context = new PortalDbContext(
                TestDatabase.CreateOptions(database.Path, defaultTimeoutSeconds: 5));
            await start.Task;

            try
            {
                await PortalDbInitializer.InitializeAsync(context, "FirstAdmin", "already-hashed");
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }).ToArray();

        start.SetResult();
        var errors = (await Task.WhenAll(tasks)).OfType<Exception>().ToList();

        Assert.Empty(errors);
        database.Context.ChangeTracker.Clear();
        Assert.Single(await database.Context.PortalSettings.ToListAsync());
        Assert.Single(await database.Context.AdminUsers.ToListAsync());
    }

    [Fact]
    public async Task Initializer_honors_cancellation_while_waiting_for_sqlite_write_lock()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        await using var context = new PortalDbContext(
            TestDatabase.CreateOptions(database.Path, defaultTimeoutSeconds: 1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await using (database.Connection.BeginTransaction(deferred: false))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                PortalDbInitializer.InitializeAsync(
                    context,
                    "FirstAdmin",
                    "already-hashed",
                    cancellation.Token));
        }

        await PortalDbInitializer.InitializeAsync(context, "FirstAdmin", "already-hashed");
        Assert.Single(await context.PortalSettings.ToListAsync());
        Assert.Single(await context.AdminUsers.ToListAsync());
    }

    [Fact]
    public async Task Audit_entry_actor_action_target_and_timestamp_persist()
    {
        await using var database = await TestDatabase.CreateAsync();
        var created = new DateTime(2026, 9, 22, 12, 30, 0, DateTimeKind.Utc);
        database.Context.AuditEntries.Add(new AuditEntry
        {
            Actor = "admin:first",
            Action = "registration.disable",
            Target = "registration:123",
            DetailsJson = "{\"reason\":\"requested\"}",
            CreatedUtc = created
        });

        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var audit = Assert.Single(await database.Context.AuditEntries.ToListAsync());
        Assert.Equal("admin:first", audit.Actor);
        Assert.Equal("registration.disable", audit.Action);
        Assert.Equal("registration:123", audit.Target);
        Assert.Equal(created, audit.CreatedUtc);
        Assert.Equal(DateTimeKind.Utc, audit.CreatedUtc.Kind);
    }

    [Fact]
    public async Task Expired_registrations_can_be_filtered_in_migrated_sqlite_database()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var cutoff = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        var expired = CreateRegistration("expired@example.test", "EXPIRED@EXAMPLE.TEST");
        expired.VerificationExpiresUtc = cutoff.AddMinutes(-1);
        var active = CreateRegistration("active@example.test", "ACTIVE@EXAMPLE.TEST");
        active.VerificationExpiresUtc = cutoff.AddMinutes(1);
        database.Context.Registrations.AddRange(expired, active);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var expiredIds = await database.Context.Registrations
            .Where(registration => registration.VerificationExpiresUtc < cutoff)
            .Select(registration => registration.Id)
            .ToListAsync();

        Assert.Equal([expired.Id], expiredIds);
    }

    [Fact]
    public async Task Audit_entries_can_be_ordered_by_timestamp_in_migrated_sqlite_database()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var first = new AuditEntry
        {
            Actor = "admin:first",
            Action = "registration.view",
            Target = "registration:first",
            CreatedUtc = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc)
        };
        var second = new AuditEntry
        {
            Actor = "admin:first",
            Action = "registration.view",
            Target = "registration:second",
            CreatedUtc = first.CreatedUtc.AddMinutes(1)
        };
        database.Context.AuditEntries.AddRange(second, first);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var targets = await database.Context.AuditEntries
            .OrderBy(entry => entry.CreatedUtc)
            .Select(entry => entry.Target)
            .ToListAsync();

        Assert.Equal([first.Target, second.Target], targets);
    }

    [Fact]
    public async Task Registration_query_indexes_exist()
    {
        await using var database = await TestDatabase.CreateAsync();

        var indexes = await ReadIndexColumnsAsync(database.Connection, "Registrations");

        Assert.Contains(indexes, index => index.SequenceEqual(["NormalizedEmail"]));
        Assert.Contains(indexes, index => index.SequenceEqual(["Status"]));
        Assert.Contains(indexes, index => index.SequenceEqual(["VerificationTokenHash"]));
        Assert.Contains(indexes, index => index.SequenceEqual(["QueueRequestId"]));
        Assert.Contains(
            indexes,
            index => index.SequenceEqual(["Status", "QueueLastCheckedUtc", "UpdatedUtc", "Id"]));
    }

    [Fact]
    public async Task Initial_migration_applies_cleanly()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();

        var tables = await ReadTableNamesAsync(database.Connection);

        Assert.Contains("Registrations", tables);
        Assert.Contains("PortalSettings", tables);
        Assert.Contains("AdminUsers", tables);
        Assert.Contains("AuditEntries", tables);
        Assert.Contains("__EFMigrationsHistory", tables);
        Assert.Equal(8, (await database.Context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Empty(await database.Context.Database.GetPendingMigrationsAsync());
        Assert.False(database.Context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Existing_email_delivery_state_migration_upgrades_through_language_migration()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mir3-web-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            await using var context = new PortalDbContext(TestDatabase.CreateOptions(path));
            var migrator = context.GetService<IMigrator>();

            await migrator.MigrateAsync("20260923125407_AddEmailDeliveryState");
            Assert.Equal(
                "20260923125407_AddEmailDeliveryState",
                (await context.Database.GetAppliedMigrationsAsync()).Last());

            // Existing rows must survive SQLite's table rebuild and receive
            // the English default for registrations created before this release.
            var existingId = Guid.NewGuid();
            var existingPasswordHash = new byte[36];
            var existingTokenHash = new byte[32];
            var sourceIpHash = new byte[32];
            var now = new DateTime(2026, 9, 23, 13, 0, 0, DateTimeKind.Utc);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Registrations"
                    ("Id", "Email", "NormalizedEmail", "PasswordHash", "Status",
                     "VerificationTokenHash", "VerificationExpiresUtc", "CreatedUtc", "UpdatedUtc", "SourceIpHash")
                VALUES ({existingId}, {"prior@example.test"}, {"prior@example.test"}, {existingPasswordHash},
                        {"PendingEmail"}, {existingTokenHash}, {now.AddHours(24)}, {now}, {now}, {sourceIpHash})
                """);
            await context.Database.MigrateAsync();

            Assert.Equal(8, (await context.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            Assert.False(context.Database.HasPendingModelChanges());
            var columns = await ReadColumnNamesAsync(context.Database.GetDbConnection(), "Registrations");
            Assert.Contains("EmailLastErrorCode", columns);
            Assert.Contains("EmailDeliveryAttemptId", columns);
            Assert.Contains("EmailDeliveryAttemptAcquiredUtc", columns);
            Assert.Contains("InitialEmailDeliveryPending", columns);
            Assert.Contains("QueuePublicationState", columns);
            Assert.Contains("PreferredLanguage", columns);
            var existing = await context.Registrations.AsNoTracking().SingleAsync(item => item.Id == existingId);
            Assert.Equal("prior@example.test", existing.NormalizedEmail);
            Assert.Equal(existingPasswordHash, existing.PasswordHash);
            Assert.Equal("en", existing.PreferredLanguage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Registration CreateRegistration(string email, string normalizedEmail)
    {
        var now = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);
        return new Registration
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = normalizedEmail,
            PasswordHash = new byte[36],
            Status = RegistrationStatus.PendingEmail,
            VerificationTokenHash = new byte[32],
            VerificationExpiresUtc = now.AddHours(1),
            CreatedUtc = now,
            UpdatedUtc = now,
            SourceIpHash = new byte[32]
        };
    }

    private static void SetHash(Registration registration, string hash, byte[] value)
    {
        switch (hash)
        {
            case "password":
                registration.PasswordHash = value;
                break;
            case "token":
                registration.VerificationTokenHash = value;
                break;
            case "ip":
                registration.SourceIpHash = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(hash));
        }
    }

    private static async Task<List<string[]>> ReadIndexColumnsAsync(SqliteConnection connection, string table)
    {
        var result = new List<string[]>();
        await using var indexesCommand = connection.CreateCommand();
        indexesCommand.CommandText = $"PRAGMA index_list('{table.Replace("'", "''", StringComparison.Ordinal)}')";
        await using var indexReader = await indexesCommand.ExecuteReaderAsync();
        var names = new List<string>();
        while (await indexReader.ReadAsync())
        {
            names.Add(indexReader.GetString(1));
        }

        foreach (var name in names)
        {
            await using var columnsCommand = connection.CreateCommand();
            columnsCommand.CommandText = $"PRAGMA index_info('{name.Replace("'", "''", StringComparison.Ordinal)}')";
            await using var columnsReader = await columnsCommand.ExecuteReaderAsync();
            var columns = new List<string>();
            while (await columnsReader.ReadAsync())
            {
                columns.Add(columnsReader.GetString(2));
            }

            result.Add([.. columns]);
        }

        return result;
    }

    private static async Task<HashSet<string>> ReadTableNamesAsync(SqliteConnection connection)
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static async Task<HashSet<string>> ReadColumnNamesAsync(System.Data.Common.DbConnection connection, string table)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table.Replace("'", "''", StringComparison.Ordinal)}')";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _path;

        private TestDatabase(string path, SqliteConnection connection, PortalDbContext context)
        {
            _path = path;
            Connection = connection;
            Context = context;
        }

        public SqliteConnection Connection { get; }

        public PortalDbContext Context { get; }

        public string Path => _path;

        public static Task<TestDatabase> CreateAsync() => CreateDatabaseAsync();

        public static Task<TestDatabase> CreateMigratedAsync(params IInterceptor[] interceptors) =>
            CreateDatabaseAsync(interceptors);

        public static DbContextOptions<PortalDbContext> CreateOptions(
            string path,
            int defaultTimeoutSeconds = 30) =>
            new DbContextOptionsBuilder<PortalDbContext>()
                .UseSqlite($"Data Source={path};Default Timeout={defaultTimeoutSeconds}")
                .Options;

        private static async Task<TestDatabase> CreateDatabaseAsync(params IInterceptor[] interceptors)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mir3-web-tests-{Guid.NewGuid():N}.db");
            var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PortalDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(interceptors)
                .Options;
            var context = new PortalDbContext(options);
            await context.Database.MigrateAsync();

            return new TestDatabase(path, connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
            File.Delete(_path);
        }
    }

    private sealed class CancelAfterSaveInterceptor(CancellationTokenSource cancellation)
        : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
