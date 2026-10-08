using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Mir3.Web.Data;
using Mir3.Web.Domain;
using Mir3.Web.Security;

namespace Mir3.Web.Tests;

public sealed class VerificationTokenServiceTests
{
    private static readonly DateTime IssuedUtc = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Issue_UsesExactly32RandomBytesBeforeBase64UrlEncoding()
    {
        var registration = CreateRegistration();
        var service = new VerificationTokenService(new MutableTimeProvider(IssuedUtc));

        var token = service.Issue(registration);
        var rawToken = WebEncoders.Base64UrlDecode(token);

        Assert.Equal(32, rawToken.Length);
        Assert.Equal(43, token.Length);
    }

    [Fact]
    public void Issue_StoresOnlySha256HashAndExpiresExactly24HoursLater()
    {
        var registration = CreateRegistration();
        var service = new VerificationTokenService(new MutableTimeProvider(IssuedUtc));

        var token = service.Issue(registration);
        var rawToken = WebEncoders.Base64UrlDecode(token);

        Assert.Equal(32, registration.VerificationTokenHash.Length);
        Assert.Equal(SHA256.HashData(rawToken), registration.VerificationTokenHash);
        Assert.NotEqual(rawToken, registration.VerificationTokenHash);
        Assert.Equal(IssuedUtc.AddHours(24), registration.VerificationExpiresUtc);
        Assert.Null(registration.VerificationUsedUtc);
    }

    [Fact]
    public void Issue_RotatesTokenAndResetsPriorConsumption()
    {
        var registration = CreateRegistration();
        registration.VerificationUsedUtc = IssuedUtc.AddMinutes(-1);
        var clock = new MutableTimeProvider(IssuedUtc);
        var service = new VerificationTokenService(clock);
        var first = service.Issue(registration);
        var firstHash = registration.VerificationTokenHash.ToArray();

        clock.UtcNow = IssuedUtc.AddMinutes(5);
        var second = service.Issue(registration);

        Assert.NotEqual(first, second);
        Assert.NotEqual(firstHash, registration.VerificationTokenHash);
        Assert.Null(registration.VerificationUsedUtc);
        Assert.Equal(clock.UtcNow.AddHours(24), registration.VerificationExpiresUtc);
    }

    [Fact]
    public async Task ConsumeAsync_ConsumesTokenExactlyOnceInMigratedSqlite()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var clock = new MutableTimeProvider(IssuedUtc);
        var service = new VerificationTokenService(clock);
        var registration = CreateRegistration();
        var token = service.Issue(registration);
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var first = await service.ConsumeAsync(database.Context, token);
        var second = await service.ConsumeAsync(database.Context, token);

        Assert.True(first);
        Assert.False(second);
        var stored = await database.Context.Registrations.SingleAsync();
        Assert.Equal(IssuedUtc, stored.VerificationUsedUtc);
    }

    [Fact]
    public async Task ConsumeAsync_ConcurrentAttemptsCannotBothSucceed()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var clock = new MutableTimeProvider(IssuedUtc);
        var service = new VerificationTokenService(clock);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var registration = CreateRegistration();
            registration.Email = $"player-{attempt}@example.test";
            registration.NormalizedEmail = $"PLAYER-{attempt}@EXAMPLE.TEST";
            var token = service.Issue(registration);
            database.Context.Registrations.Add(registration);
            await database.Context.SaveChangesAsync();
            database.Context.ChangeTracker.Clear();

            await using var firstContext = new PortalDbContext(TestDatabase.CreateOptions(database.Path));
            await using var secondContext = new PortalDbContext(TestDatabase.CreateOptions(database.Path));
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstAttempt = ConsumeAfterStartAsync(service, firstContext, token, start.Task);
            var secondAttempt = ConsumeAfterStartAsync(service, secondContext, token, start.Task);

            start.SetResult();
            var results = await Task.WhenAll(firstAttempt, secondAttempt);

            Assert.Equal(1, results.Count(result => result));
            await using var verificationContext = new PortalDbContext(TestDatabase.CreateOptions(database.Path));
            Assert.Equal(
                IssuedUtc,
                (await verificationContext.Registrations.SingleAsync(item => item.Id == registration.Id))
                .VerificationUsedUtc);
        }
    }

    [Fact]
    public async Task ConsumeAsync_RejectsTokenAtExactExpiryBoundary()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var clock = new MutableTimeProvider(IssuedUtc);
        var service = new VerificationTokenService(clock);
        var registration = CreateRegistration();
        var token = service.Issue(registration);
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        clock.UtcNow = IssuedUtc.AddHours(24);

        var consumed = await service.ConsumeAsync(database.Context, token);

        Assert.False(consumed);
        Assert.Null((await database.Context.Registrations.SingleAsync()).VerificationUsedUtc);
    }

    [Fact]
    public async Task ConsumeAsync_AcceptsTokenImmediatelyBeforeExpiry()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var clock = new MutableTimeProvider(IssuedUtc);
        var service = new VerificationTokenService(clock);
        var registration = CreateRegistration();
        var token = service.Issue(registration);
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        clock.UtcNow = IssuedUtc.AddHours(24).AddTicks(-1);

        Assert.True(await service.ConsumeAsync(database.Context, token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not+base64/url")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("A")]
    [InlineData("AA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task ConsumeAsync_RejectsMalformedTokensWithoutMutation(string? token)
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var service = new VerificationTokenService(new MutableTimeProvider(IssuedUtc));
        var registration = CreateRegistration();
        service.Issue(registration);
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var consumed = await service.ConsumeAsync(database.Context, token);

        Assert.False(consumed);
        Assert.Null((await database.Context.Registrations.SingleAsync()).VerificationUsedUtc);
    }

    [Fact]
    public async Task ConsumeAsync_RejectsExtremelyOversizedTokenBeforeProportionalAllocationOrDatabaseMutation()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var service = new VerificationTokenService(new MutableTimeProvider(IssuedUtc));
        var registration = CreateRegistration();
        service.Issue(registration);
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        var oversizedToken = new string('A', 4 * 1024 * 1024);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var consumeTask = service.ConsumeAsync(database.Context, oversizedToken);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(consumeTask.IsCompletedSuccessfully);
        Assert.False(await consumeTask);
        Assert.InRange(allocatedBytes, 0, 16 * 1024);
        Assert.Null((await database.Context.Registrations.SingleAsync()).VerificationUsedUtc);
    }

    [Fact]
    public async Task ConsumeAsync_WrongWellFormedTokenDoesNotRevealOrMutateRegistration()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var service = new VerificationTokenService(new MutableTimeProvider(IssuedUtc));
        var registration = CreateRegistration();
        service.Issue(registration);
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        var wrongToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        Assert.False(await service.ConsumeAsync(database.Context, wrongToken));
        Assert.Null((await database.Context.Registrations.SingleAsync()).VerificationUsedUtc);
    }

    private static Registration CreateRegistration() => new()
    {
        Id = Guid.NewGuid(),
        Email = "player@example.test",
        NormalizedEmail = "PLAYER@EXAMPLE.TEST",
        PasswordHash = new byte[36],
        Status = RegistrationStatus.PendingEmail,
        VerificationTokenHash = new byte[32],
        VerificationExpiresUtc = IssuedUtc,
        CreatedUtc = IssuedUtc,
        UpdatedUtc = IssuedUtc,
        SourceIpHash = new byte[32]
    };

    private static async Task<bool> ConsumeAfterStartAsync(
        VerificationTokenService service,
        PortalDbContext database,
        string token,
        Task start)
    {
        await start;
        return await service.ConsumeAsync(database, token);
    }

    private sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private TestDatabase(string path, PortalDbContext context)
        {
            Path = path;
            Context = context;
        }

        public string Path { get; }

        public PortalDbContext Context { get; }

        public static DbContextOptions<PortalDbContext> CreateOptions(string path) =>
            new DbContextOptionsBuilder<PortalDbContext>()
                .UseSqlite($"Data Source={path};Default Timeout=30")
                .Options;

        public static async Task<TestDatabase> CreateMigratedAsync()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"mir3-token-tests-{Guid.NewGuid():N}.db");
            var context = new PortalDbContext(CreateOptions(path));
            await context.Database.MigrateAsync();
            return new TestDatabase(path, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            File.Delete(Path);
        }
    }
}
