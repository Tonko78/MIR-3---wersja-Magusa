using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Mir3.Web.Data;
using Mir3.Web.Domain;
using Mir3.Web.Options;
using Mir3.Web.Pages;
using Mir3.Web.Security;
using Mir3.Web.Services;

namespace Mir3.Web.Tests;

public sealed class RegistrationServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Polish_registration_persists_preference_for_delayed_email_delivery()
    {
        await using var fixture = await ServiceFixture.CreateAsync();
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pl-PL");
            var result = await fixture.Service.RegisterAsync("polish@example.test", "Secret7", IPAddress.Parse("203.0.113.21"));
            Assert.Equal(RegistrationCreateOutcome.Created, result.Outcome);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }

        var registration = Assert.Single(await fixture.Read.Registrations.ToListAsync());
        Assert.Equal("pl", registration.PreferredLanguage);
        await fixture.Outbox.ProcessOnceAsync();
        Assert.Contains("Zweryfikuj", Assert.Single(fixture.Sender.Messages).Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Valid_registration_persists_only_hashes_and_sends_verification_mail()
    {
        await using var fixture = await ServiceFixture.CreateAsync();

        var result = await fixture.Service.RegisterAsync(
            " Player@Example.Test ",
            "Secret7",
            IPAddress.Parse("203.0.113.42"));

        Assert.Equal(RegistrationCreateOutcome.Created, result.Outcome);
        var stored = Assert.Single(await fixture.Read.Registrations.ToListAsync());
        Assert.Equal("player@example.test", stored.NormalizedEmail);
        Assert.Equal(RegistrationStatus.PendingEmail, stored.Status);
        Assert.True(GamePasswordHasher.Verify("Secret7", stored.PasswordHash));
        Assert.Equal(36, stored.PasswordHash.Length);
        Assert.Equal(32, stored.SourceIpHash.Length);
        Assert.NotEqual(SHA256.HashData(Encoding.UTF8.GetBytes("203.0.113.42")), stored.SourceIpHash);
        Assert.True(stored.InitialEmailDeliveryPending);
        Assert.Empty(fixture.Sender.Messages);

        await fixture.Outbox.ProcessOnceAsync();

        Assert.Single(fixture.Sender.Messages);
        stored = await fixture.ReadRegistrationAsync(stored.Id);
        Assert.False(stored.InitialEmailDeliveryPending);

        var persistedStrings = typeof(Registration).GetProperties()
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.GetValue(stored) as string)
            .Where(value => value is not null);
        Assert.DoesNotContain("Secret7", persistedStrings);
        Assert.DoesNotContain(fixture.Logs.Messages, message => message.Contains("Secret7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Duplicate_normalized_email_returns_the_same_generic_conflict_for_all_states()
    {
        foreach (var status in Enum.GetValues<RegistrationStatus>())
        {
            await using var fixture = await ServiceFixture.CreateAsync();
            await fixture.SeedAsync("player@example.test", status);

            var result = await fixture.Service.RegisterAsync(
                " PLAYER@example.test ",
                "Secret7",
                IPAddress.Parse("203.0.113.43"));

            Assert.Equal(RegistrationCreateOutcome.Conflict, result.Outcome);
            Assert.Equal(RegistrationMessages.Created, result.UserMessage);
            Assert.Null(result.RegistrationReference);
            Assert.DoesNotContain(status.ToString(), result.UserMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(fixture.Sender.Messages);
        }
    }

    [Fact]
    public async Task Expired_never_verified_pending_registration_is_restarted_in_place_with_new_credentials()
    {
        await using var fixture = await ServiceFixture.CreateAsync();
        var (registration, oldToken) = await fixture.SeedWithTokenAsync(expired: true);
        var oldPasswordHash = registration.PasswordHash.ToArray();
        registration.EmailLastErrorCode = "smtp-send";
        registration.EmailDeliveryAttemptId = Guid.NewGuid();
        registration.EmailDeliveryAttemptAcquiredUtc = Now.AddMinutes(-10);
        registration.InitialEmailDeliveryPending = false;
        await fixture.Write.SaveChangesAsync();

        var result = await fixture.Service.RegisterAsync(
            " PLAYER@example.test ",
            "Changed8",
            IPAddress.Parse("203.0.113.88"));

        Assert.Equal(RegistrationCreateOutcome.Created, result.Outcome);
        Assert.Null(result.RegistrationReference);
        var stored = Assert.Single(await fixture.Read.Registrations.AsNoTracking().ToListAsync());
        Assert.Equal(registration.Id, stored.Id);
        Assert.True(GamePasswordHasher.Verify("Changed8", stored.PasswordHash));
        Assert.False(oldPasswordHash.SequenceEqual(stored.PasswordHash));
        Assert.True(stored.InitialEmailDeliveryPending);
        Assert.Null(stored.EmailLastErrorCode);
        Assert.Null(stored.EmailDeliveryAttemptId);
        Assert.Null(stored.EmailDeliveryAttemptAcquiredUtc);
        Assert.Null(stored.VerificationUsedUtc);
        Assert.Null(stored.EmailVerifiedUtc);
        Assert.Equal(RegistrationVerificationOutcome.Invalid, (await fixture.Service.VerifyAsync(oldToken)).Outcome);

        await fixture.Outbox.ProcessOnceAsync();
        var message = Assert.Single(fixture.Sender.Messages);
        var newToken = Assert.Single(Regex.Matches(message.TextBody, @"token=([A-Za-z0-9_-]{43})"))
            .Groups[1].Value;
        Assert.NotEqual(oldToken, newToken);
        Assert.Equal(RegistrationVerificationOutcome.Verified, (await fixture.Service.VerifyAsync(newToken)).Outcome);
    }

    [Fact]
    public async Task Concurrent_expired_replacements_keep_one_identity_and_one_winning_password()
    {
        await using var fixture = await ServiceFixture.CreateAsync();
        var (registration, oldToken) = await fixture.SeedWithTokenAsync(expired: true);

        var results = await Task.WhenAll(
            fixture.Service.RegisterAsync("player@example.test", "Changed8", IPAddress.Parse("203.0.113.89")),
            fixture.Service.RegisterAsync("PLAYER@example.test", "Changed9", IPAddress.Parse("203.0.113.90")));

        Assert.All(results, result =>
        {
            Assert.Equal(RegistrationMessages.Created, result.UserMessage);
            Assert.Null(result.RegistrationReference);
        });
        Assert.Single(results, result => result.Outcome == RegistrationCreateOutcome.Created);
        var stored = Assert.Single(await fixture.Read.Registrations.AsNoTracking().ToListAsync());
        Assert.Equal(registration.Id, stored.Id);
        Assert.True(
            GamePasswordHasher.Verify("Changed8", stored.PasswordHash) ^
            GamePasswordHasher.Verify("Changed9", stored.PasswordHash));
        Assert.Equal(RegistrationVerificationOutcome.Invalid, (await fixture.Service.VerifyAsync(oldToken)).Outcome);
    }

    [Theory]
    [InlineData(RegistrationStatus.PendingEmail)]
    [InlineData(RegistrationStatus.AwaitingAdmin)]
    [InlineData(RegistrationStatus.QueuePending)]
    [InlineData(RegistrationStatus.Active)]
    [InlineData(RegistrationStatus.Disabled)]
    [InlineData(RegistrationStatus.Failed)]
    public async Task Nonexpired_or_nonpending_duplicate_is_not_mutated(RegistrationStatus status)
    {
        await using var fixture = await ServiceFixture.CreateAsync();
        var registration = await fixture.SeedAsync("player@example.test", status);
        var before = System.Text.Json.JsonSerializer.Serialize(registration);

        var result = await fixture.Service.RegisterAsync(
            "PLAYER@example.test",
            "Changed8",
            IPAddress.Parse("203.0.113.91"));

        Assert.Equal(RegistrationCreateOutcome.Conflict, result.Outcome);
        Assert.Null(result.RegistrationReference);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(await fixture.ReadRegistrationAsync(registration.Id)));
    }

    [Fact]
    public async Task Failed_initial_delivery_can_be_requeued_without_exposing_a_reference_or_changing_password()
    {
        await using var fixture = await ServiceFixture.CreateAsync();
        var registration = await fixture.SeedAsync("player@example.test", RegistrationStatus.PendingEmail);
        registration.EmailLastErrorCode = "smtp-send";
        registration.InitialEmailDeliveryPending = false;
        var originalHash = registration.PasswordHash.ToArray();
        await fixture.Write.SaveChangesAsync();

        var previousCulture = CultureInfo.CurrentUICulture;
        RegistrationCreateResult result;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pl-PL");
            result = await fixture.Service.RegisterAsync(
                "player@example.test", "Changed8", IPAddress.Parse("203.0.113.92"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }

        Assert.Equal(RegistrationCreateOutcome.Conflict, result.Outcome);
        Assert.Null(result.RegistrationReference);
        var stored = await fixture.ReadRegistrationAsync(registration.Id);
        Assert.Equal(originalHash, stored.PasswordHash);
        Assert.True(stored.InitialEmailDeliveryPending);
        Assert.Equal("pl", stored.PreferredLanguage);
        await fixture.Outbox.ProcessOnceAsync();
        Assert.StartsWith("Zweryfikuj adres e-mail", Assert.Single(fixture.Sender.Messages).Subject, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("used")]
    [InlineData("verified")]
    public async Task Failed_delivery_recovery_does_not_mutate_used_or_verified_pending_state(string state)
    {
        await using var fixture = await ServiceFixture.CreateAsync();
        var registration = await fixture.SeedAsync("player@example.test", RegistrationStatus.PendingEmail);
        registration.EmailLastErrorCode = "smtp-send";
        registration.InitialEmailDeliveryPending = false;
        if (state == "used") registration.VerificationUsedUtc = Now.AddMinutes(-1);
        if (state == "verified") registration.EmailVerifiedUtc = Now.AddMinutes(-1);
        await fixture.Write.SaveChangesAsync();
        var before = System.Text.Json.JsonSerializer.Serialize(registration);

        var result = await fixture.Service.RegisterAsync(
            "player@example.test",
            "Changed8",
            IPAddress.Parse("203.0.113.93"));

        Assert.Equal(RegistrationCreateOutcome.Conflict, result.Outcome);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(await fixture.ReadRegistrationAsync(registration.Id)));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("")]
    public async Task Invalid_token_does_not_change_registration(string token)
    {
        await using var fixture = await ServiceFixture.CreateAsync();
        var registration = await fixture.SeedAsync("player@example.test", RegistrationStatus.PendingEmail);
        var before = registration.UpdatedUtc;

        var result = await fixture.Service.VerifyAsync(token);

        Assert.Equal(RegistrationVerificationOutcome.Invalid, result.Outcome);
        var stored = await fixture.ReadRegistrationAsync(registration.Id);
        Assert.Equal(RegistrationStatus.PendingEmail, stored.Status);
        Assert.Null(stored.VerificationUsedUtc);
        Assert.Equal(before, stored.UpdatedUtc);
    }

    [Fact]
    public async Task Expired_token_does_not_change_registration()
    {
        await using var fixture = await ServiceFixture.CreateAsync();
        var (registration, token) = await fixture.SeedWithTokenAsync(expired: true);

        var result = await fixture.Service.VerifyAsync(token);

        Assert.Equal(RegistrationVerificationOutcome.Invalid, result.Outcome);
        Assert.Equal(registration.Id, result.RegistrationReference);
        Assert.True(result.CanResend);
        var stored = await fixture.ReadRegistrationAsync(registration.Id);
        Assert.Equal(RegistrationStatus.PendingEmail, stored.Status);
        Assert.Null(stored.VerificationUsedUtc);
    }

    [Fact]
    public async Task Used_token_does_not_change_registration()
    {
        await using var fixture = await ServiceFixture.CreateAsync();
        var (registration, token) = await fixture.SeedWithTokenAsync(used: true);
        var usedAt = registration.VerificationUsedUtc;

        var result = await fixture.Service.VerifyAsync(token);

        Assert.Equal(RegistrationVerificationOutcome.Invalid, result.Outcome);
        Assert.Null(result.RegistrationReference);
        Assert.False(result.CanResend);
        var stored = await fixture.ReadRegistrationAsync(registration.Id);
        Assert.Equal(RegistrationStatus.PendingEmail, stored.Status);
        Assert.Equal(usedAt, stored.VerificationUsedUtc);
    }

    [Fact]
    public async Task Manual_verification_commits_AwaitingAdmin_without_queue_command()
    {
        await using var fixture = await ServiceFixture.CreateAsync(autoActivate: false);
        var (registration, token) = await fixture.SeedWithTokenAsync();

        var result = await fixture.Service.VerifyAsync(token);

        Assert.Equal(RegistrationVerificationOutcome.Verified, result.Outcome);
        var stored = await fixture.ReadRegistrationAsync(registration.Id);
        Assert.Equal(RegistrationStatus.AwaitingAdmin, stored.Status);
        Assert.NotNull(stored.VerificationUsedUtc);
        Assert.Null(stored.QueueRequestId);
        Assert.Empty(Directory.EnumerateFiles(fixture.QueueIncomingPath));
    }

    [Fact]
    public async Task Automatic_verification_commits_then_publishes_CreateAccount_with_existing_hash()
    {
        await using var fixture = await ServiceFixture.CreateAsync(autoActivate: true);
        var (registration, token) = await fixture.SeedWithTokenAsync();

        var result = await fixture.Service.VerifyAsync(token);

        Assert.Equal(RegistrationVerificationOutcome.Verified, result.Outcome);
        var stored = await fixture.ReadRegistrationAsync(registration.Id);
        Assert.Equal(RegistrationStatus.QueuePending, stored.Status);
        Assert.NotNull(stored.QueueRequestId);
        Assert.Equal(QueuePublicationState.Published, stored.QueuePublicationState);
        Assert.NotNull(stored.VerificationUsedUtc);
        var commandPath = Assert.Single(Directory.EnumerateFiles(fixture.QueueIncomingPath));
        var commandJson = await File.ReadAllTextAsync(commandPath);
        using var command = System.Text.Json.JsonDocument.Parse(commandJson);
        Assert.Equal(
            (int)AccountPortal.Contracts.AccountCommandType.CreateAccount,
            command.RootElement.GetProperty("Type").GetInt32());
        Assert.Equal(
            Convert.ToBase64String(registration.PasswordHash),
            command.RootElement.GetProperty("PasswordHashBase64").GetString());
        Assert.DoesNotContain("Secret7", commandJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verification_does_not_report_success_or_consume_token_when_commit_fails()
    {
        var operations = new RegistrationServiceOperations
        {
            BeforeVerificationCommit = _ => throw new InvalidOperationException("forced commit failure")
        };
        await using var fixture = await ServiceFixture.CreateAsync(operations: operations);
        var (registration, token) = await fixture.SeedWithTokenAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.VerifyAsync(token));

        var stored = await fixture.ReadRegistrationAsync(registration.Id);
        Assert.Equal(RegistrationStatus.PendingEmail, stored.Status);
        Assert.Null(stored.VerificationUsedUtc);
        Assert.Null(stored.EmailVerifiedUtc);
    }

    [Fact]
    public async Task Cancellation_at_commit_boundary_does_not_make_a_committed_verification_look_unused()
    {
        using var cancellation = new CancellationTokenSource();
        var operations = new RegistrationServiceOperations
        {
            BeforeVerificationCommit = _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }
        };
        await using var fixture = await ServiceFixture.CreateAsync(autoActivate: true, operations: operations);
        var (registration, token) = await fixture.SeedWithTokenAsync();

        var result = await fixture.Service.VerifyAsync(token, cancellation.Token);

        Assert.Equal(RegistrationVerificationOutcome.Verified, result.Outcome);
        var stored = await fixture.ReadRegistrationAsync(registration.Id);
        Assert.Equal(RegistrationStatus.QueuePending, stored.Status);
        Assert.NotNull(stored.VerificationUsedUtc);
        Assert.Equal(RegistrationVerificationOutcome.Invalid, (await fixture.Service.VerifyAsync(token)).Outcome);
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("cancellation")]
    public async Task Verification_success_survives_best_effort_post_commit_kick_failure(string failure)
    {
        var operations = new RegistrationServiceOperations
        {
            AfterVerificationCommit = _ => failure == "cancellation"
                ? Task.FromCanceled(new CancellationToken(canceled: true))
                : Task.FromException(new IOException("forced post-commit kick failure"))
        };
        await using var fixture = await ServiceFixture.CreateAsync(autoActivate: true, operations: operations);
        var (registration, token) = await fixture.SeedWithTokenAsync();

        var result = await fixture.Service.VerifyAsync(token);

        Assert.Equal(RegistrationVerificationOutcome.Verified, result.Outcome);
        var stored = await fixture.ReadRegistrationAsync(registration.Id);
        Assert.Equal(RegistrationStatus.QueuePending, stored.Status);
        Assert.NotNull(stored.VerificationUsedUtc);
        Assert.Equal(QueuePublicationState.Pending, stored.QueuePublicationState);
        Assert.Empty(Directory.EnumerateFiles(fixture.QueueIncomingPath, "*.json"));
        Assert.Equal(RegistrationVerificationOutcome.Invalid, (await fixture.Service.VerifyAsync(token)).Outcome);
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("cancellation")]
    public async Task Automatic_verification_recovers_failed_publication_after_restart_with_same_request_id(string failure)
    {
        var queueOperations = new AccountQueueClientOperations
        {
            BeforeCommandRename = failure == "cancellation"
                ? () => throw new OperationCanceledException("forced cancellation")
                : () => throw new IOException("forced write failure")
        };
        await using var fixture = await ServiceFixture.CreateAsync(autoActivate: true, queueOperations: queueOperations);
        var (registration, token) = await fixture.SeedWithTokenAsync();

        var result = await fixture.Service.VerifyAsync(token);

        Assert.Equal(RegistrationVerificationOutcome.Verified, result.Outcome);
        var pending = await fixture.ReadRegistrationAsync(registration.Id);
        var stableRequestId = Assert.IsType<Guid>(pending.QueueRequestId);
        Assert.Equal(QueuePublicationState.Pending, pending.QueuePublicationState);
        Assert.Empty(Directory.EnumerateFiles(fixture.QueueIncomingPath, "*.json"));

        await fixture.CreateRestartedOutbox().ProcessOnceAsync();

        var recovered = await fixture.ReadRegistrationAsync(registration.Id);
        Assert.Equal(stableRequestId, recovered.QueueRequestId);
        Assert.Equal(QueuePublicationState.Published, recovered.QueuePublicationState);
        var commandPath = Assert.Single(Directory.EnumerateFiles(fixture.QueueIncomingPath, "*.json"));
        Assert.Contains(stableRequestId.ToString("D"), commandPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Uncertain_publication_is_persisted_and_never_republished_with_a_new_id()
    {
        var attempts = 0;
        var queueOperations = new AccountQueueClientOperations
        {
            BeforeCommandRename = () => attempts++,
            SyncDirectory = _ => throw new IOException("forced fsync failure")
        };
        await using var fixture = await ServiceFixture.CreateAsync(autoActivate: true, queueOperations: queueOperations);
        var (registration, token) = await fixture.SeedWithTokenAsync();

        var result = await fixture.Service.VerifyAsync(token);

        Assert.Equal(RegistrationVerificationOutcome.Verified, result.Outcome);
        var uncertain = await fixture.ReadRegistrationAsync(registration.Id);
        var stableRequestId = Assert.IsType<Guid>(uncertain.QueueRequestId);
        Assert.Equal(QueuePublicationState.Uncertain, uncertain.QueuePublicationState);
        Assert.Equal(1, attempts);

        await fixture.CreateRestartedOutbox().ProcessOnceAsync();

        var unchanged = await fixture.ReadRegistrationAsync(registration.Id);
        Assert.Equal(stableRequestId, unchanged.QueueRequestId);
        Assert.Equal(QueuePublicationState.Uncertain, unchanged.QueuePublicationState);
        Assert.Single(Directory.EnumerateFiles(fixture.QueueIncomingPath, "*.json"));
    }

    [Fact]
    public async Task Lost_uncertain_publication_is_republished_only_with_the_same_request_id()
    {
        var queueOperations = new AccountQueueClientOperations
        {
            SyncDirectory = _ => throw new IOException("forced fsync failure")
        };
        await using var fixture = await ServiceFixture.CreateAsync(autoActivate: true, queueOperations: queueOperations);
        var (registration, token) = await fixture.SeedWithTokenAsync();

        await fixture.Service.VerifyAsync(token);

        var uncertain = await fixture.ReadRegistrationAsync(registration.Id);
        var stableRequestId = Assert.IsType<Guid>(uncertain.QueueRequestId);
        var lostPath = Assert.Single(Directory.EnumerateFiles(fixture.QueueIncomingPath, "*.json"));
        File.Delete(lostPath);

        await fixture.CreateRestartedOutbox().ProcessOnceAsync();

        var recovered = await fixture.ReadRegistrationAsync(registration.Id);
        Assert.Equal(stableRequestId, recovered.QueueRequestId);
        Assert.Equal(QueuePublicationState.Published, recovered.QueuePublicationState);
        var recoveredPath = Assert.Single(Directory.EnumerateFiles(fixture.QueueIncomingPath, "*.json"));
        Assert.Contains(stableRequestId.ToString("D"), recoveredPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resend_only_sends_for_pending_email_registration()
    {
        await using var fixture = await ServiceFixture.CreateAsync();
        var pending = await fixture.SeedAsync("pending@example.test", RegistrationStatus.PendingEmail);
        var active = await fixture.SeedAsync("active@example.test", RegistrationStatus.Active);

        await fixture.Service.ResendVerificationAsync(pending.Id);
        await fixture.Service.ResendVerificationAsync(active.Id);

        Assert.Single(fixture.Sender.Messages);
    }

    [Fact]
    public async Task Registration_and_duplicate_paths_do_not_perform_smtp_sized_work_inline()
    {
        await using var fixture = await ServiceFixture.CreateAsync();

        var accepted = await fixture.Service.RegisterAsync("new@example.test", "Secret7", IPAddress.Parse("203.0.113.50"));
        var duplicate = await fixture.Service.RegisterAsync("new@example.test", "Secret7", IPAddress.Parse("203.0.113.50"));

        Assert.Equal(RegistrationMessages.Created, accepted.UserMessage);
        Assert.Equal(RegistrationMessages.Created, duplicate.UserMessage);
        Assert.Null(accepted.RegistrationReference);
        Assert.Null(duplicate.RegistrationReference);
        Assert.Empty(fixture.Sender.Messages);
    }

    [Fact]
    public async Task Verify_page_offers_resend_only_for_expired_pending_token()
    {
        var registration = Guid.NewGuid();
        var expired = new VerifyModel(new StubRegistrationService
        {
            VerificationResult = new RegistrationVerificationResult(
                RegistrationVerificationOutcome.Invalid,
                RegistrationMessages.VerificationInvalid,
                registration,
                CanResend: true)
        });
        var verified = new VerifyModel(new StubRegistrationService
        {
            VerificationResult = new RegistrationVerificationResult(
                RegistrationVerificationOutcome.Verified,
                RegistrationMessages.VerificationSucceeded,
                registration)
        });

        await expired.OnGetAsync("expired", CancellationToken.None);
        await verified.OnGetAsync("valid", CancellationToken.None);

        Assert.True(expired.CanResend);
        Assert.Equal(registration, expired.RegistrationReference);
        Assert.False(verified.CanResend);
        Assert.Equal(registration, verified.RegistrationReference);
    }

    [Fact]
    public async Task Register_page_keeps_generic_message_without_exposing_a_queryable_reference()
    {
        var registration = Guid.NewGuid();
        var model = new RegisterModel(new StubRegistrationService
        {
            CreateResult = new RegistrationCreateResult(
                RegistrationCreateOutcome.Created,
                RegistrationMessages.Created,
                registration)
        })
        {
            PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
            },
            Input = new RegisterModel.RegisterInput
            {
                Email = "player@example.test",
                Password = "Secret7",
                ConfirmPassword = "Secret7",
                AcceptRules = true
            }
        };
        model.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.55");

        await model.OnPostAsync(CancellationToken.None);

        Assert.Equal(RegistrationMessages.Created, model.ResultMessage);
    }

    [Fact]
    public async Task Accepted_and_duplicate_registration_http_responses_are_indistinguishable_and_offer_no_oracle_links()
    {
        using var factory = new PortalFactory("Development");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await InitializePortalDatabaseAsync(factory.Services);

        var accepted = await PostValidRegistrationAsync(client, "player@example.test", "Secret7");
        var duplicate = await PostValidRegistrationAsync(client, "player@example.test", "Secret7");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(accepted.StatusCode, duplicate.StatusCode);
        Assert.Equal(NormalizeDynamicFormValues(accepted.Body), NormalizeDynamicFormValues(duplicate.Body));
        Assert.DoesNotContain("registration reference", accepted.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/status?", accepted.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("handler=Resend", accepted.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Queue_publication_precedes_a_gated_slow_initial_email()
    {
        var sender = new GatedSender();
        await using var fixture = await ServiceFixture.CreateAsync(sender: sender);
        var pendingMail = await fixture.SeedAsync("mail@example.test", RegistrationStatus.PendingEmail);
        pendingMail.InitialEmailDeliveryPending = true;
        var queued = await fixture.SeedQueuedAsync("queue@example.test");
        await fixture.Write.SaveChangesAsync();

        var processing = fixture.Outbox.ProcessOnceAsync();

        await sender.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(Path.Combine(fixture.QueueIncomingPath, $"{queued.QueueRequestId:D}.json")));
        sender.Release.TrySetResult();
        await processing;
    }

    [Fact]
    public async Task Repeated_mail_failures_do_not_starve_queue_intents_beyond_one_batch_or_restart()
    {
        var sender = new FailingSender();
        await using var fixture = await ServiceFixture.CreateAsync(sender: sender);
        for (var index = 0; index < 51; index++)
        {
            var mail = await fixture.SeedAsync($"mail{index:D2}@example.test", RegistrationStatus.PendingEmail);
            mail.InitialEmailDeliveryPending = true;
            await fixture.SeedQueuedAsync($"queue{index:D2}@example.test");
        }
        await fixture.Write.SaveChangesAsync();

        await fixture.Outbox.ProcessOnceAsync();
        Assert.Equal(50, Directory.EnumerateFiles(fixture.QueueIncomingPath, "*.json").Count());

        await fixture.CreateRestartedOutbox().ProcessOnceAsync();
        Assert.Equal(51, Directory.EnumerateFiles(fixture.QueueIncomingPath, "*.json").Count());
        Assert.Contains("mail50@example.test", sender.AttemptedRecipients);
    }

    [Fact]
    public async Task Running_worker_publishes_a_later_queue_intent_while_initial_email_delivery_is_blocked()
    {
        var sender = new GatedSender();
        await using var fixture = await ServiceFixture.CreateAsync(sender: sender);
        var pendingMail = await fixture.SeedAsync("mail@example.test", RegistrationStatus.PendingEmail);
        pendingMail.InitialEmailDeliveryPending = true;
        for (var index = 0; index < 50; index++)
        {
            await fixture.SeedQueuedAsync($"queue{index:D2}@example.test");
        }
        await fixture.Write.SaveChangesAsync();
        using var worker = fixture.CreateOutboxWorker();

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await sender.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(
                await WaitUntilAsync(
                    () => Directory.EnumerateFiles(fixture.QueueIncomingPath, "*.json").Count() >= 50,
                    TimeSpan.FromSeconds(5)),
                "The first queue batch was not published while SMTP remained blocked.");

            var later = await fixture.SeedQueuedAsync("queue50@example.test");
            var laterPath = Path.Combine(fixture.QueueIncomingPath, $"{later.QueueRequestId:D}.json");

            Assert.True(
                await WaitUntilAsync(() => File.Exists(laterPath), TimeSpan.FromSeconds(5)),
                "The independently scheduled queue publisher did not publish the later intent while SMTP remained blocked.");
            Assert.False(sender.Release.Task.IsCompleted);
        }
        finally
        {
            sender.Release.TrySetResult();
            await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Running_worker_stops_promptly_when_initial_email_delivery_is_blocked()
    {
        var sender = new GatedSender();
        await using var fixture = await ServiceFixture.CreateAsync(sender: sender);
        var pendingMail = await fixture.SeedAsync("mail@example.test", RegistrationStatus.PendingEmail);
        pendingMail.InitialEmailDeliveryPending = true;
        await fixture.Write.SaveChangesAsync();
        using var worker = fixture.CreateOutboxWorker();

        await worker.StartAsync(CancellationToken.None);
        await sender.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(sender.Release.Task.IsCompleted);
    }

    [Fact]
    public async Task Status_lookup_returns_safe_public_state_without_internal_values()
    {
        await using var fixture = await ServiceFixture.CreateAsync();
        var registration = await fixture.SeedAsync("player@example.test", RegistrationStatus.QueuePending);
        registration.QueueRequestId = Guid.NewGuid();
        registration.QueueLastErrorCode = "queue-failed";
        await fixture.Write.SaveChangesAsync();

        var result = await fixture.Service.GetStatusAsync(registration.Id);

        Assert.Equal(RegistrationStatusOutcome.Found, result.Outcome);
        Assert.Equal("Processing", result.PublicStatus);
        Assert.DoesNotContain(registration.QueueRequestId.Value.ToString(), result.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("queue-failed", result.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("player@example.test", result.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forwarded_headers_trust_only_the_named_proxy()
    {
        using var factory = new PortalFactory("Development");
        var options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

#pragma warning disable ASPDEPR005
        Assert.Empty(options.KnownNetworks);
#pragma warning restore ASPDEPR005
        Assert.Empty(options.KnownIPNetworks);
        var proxy = Assert.Single(options.KnownProxies);
        Assert.Equal(IPAddress.Parse("192.168.0.64"), proxy);
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
    }

    [Fact]
    public async Task Registration_policy_allows_five_requests_then_returns_429()
    {
        using var factory = new PortalFactory("Development");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await client.PostAsync("/register", new FormUrlEncodedContent([]));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var rejected = await client.PostAsync("/register", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Verification_resend_policy_canonicalizes_uuid_forms_and_separates_client_ips()
    {
        using var factory = new PortalFactory("Development", trustedProxy: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var registration = Guid.NewGuid();
        var forms = new[] { registration.ToString("D"), registration.ToString("N"), $"{{{registration:D}}}" };

        foreach (var form in forms)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/verify?handler=Resend&registration={Uri.EscapeDataString(form)}")
            {
                Content = new FormUrlEncodedContent([])
            };
            request.Headers.Add("X-Forwarded-For", "198.51.100.76");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (var rejectedRequest = new HttpRequestMessage(
                   HttpMethod.Post,
                   $"/verify?handler=Resend&registration={registration:D}")
               {
                   Content = new FormUrlEncodedContent([])
               })
        {
            rejectedRequest.Headers.Add("X-Forwarded-For", "198.51.100.76");
            using var rejected = await client.SendAsync(rejectedRequest);
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        }

        using var independentIpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/verify?handler=Resend&registration={registration:D}")
        {
            Content = new FormUrlEncodedContent([])
        };
        independentIpRequest.Headers.Add("X-Forwarded-For", "198.51.100.77");
        using var independentIp = await client.SendAsync(independentIpRequest);
        Assert.Equal(HttpStatusCode.BadRequest, independentIp.StatusCode);
    }

    [Fact]
    public async Task Verification_resend_policy_uses_one_stable_partition_for_invalid_references()
    {
        using var factory = new PortalFactory("Development");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var invalid in new[] { "invalid-a", "invalid-b", "{not-a-guid}" })
        {
            using var response = await client.PostAsync(
                $"/verify?handler=Resend&registration={Uri.EscapeDataString(invalid)}",
                new FormUrlEncodedContent([]));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var rejected = await client.PostAsync(
            "/verify?handler=Resend&registration=attacker-controlled-new-value",
            new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public void Admin_login_named_rate_limit_policy_is_registered()
    {
        using var factory = new PortalFactory("Development");
        var options = factory.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        var names = typeof(RateLimiterOptions)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Where(property => property.Name is "PolicyMap" or "UnactivatedPolicyMap")
            .SelectMany(property => ((System.Collections.IDictionary)property.GetValue(options)!).Keys.Cast<string>())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("admin-login", names);
    }

    [Fact]
    public async Task Status_policy_allows_thirty_requests_per_ip_then_returns_429()
    {
        using var factory = new PortalFactory("Development");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        for (var attempt = 0; attempt < 30; attempt++)
        {
            using var response = await client.GetAsync("/status?registration=00000000-0000-0000-0000-000000000000");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var rejected = await client.GetAsync("/status?registration=00000000-0000-0000-0000-000000000000");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Production_uses_forwarded_proto_for_https_and_emits_security_headers()
    {
        using var factory = new PortalFactory("Production", trustedProxy: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/register");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "198.51.100.10");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Strict-Transport-Security"));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        var antiforgeryCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", antiforgeryCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", antiforgeryCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_rejects_spoofed_forwarded_proto_from_untrusted_peer()
    {
        using var factory = new PortalFactory("Production", trustedProxy: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/register");
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.StartsWith("https://", response.Headers.Location!.AbsoluteUri, StringComparison.Ordinal);
    }

    private static async Task InitializePortalDatabaseAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PortalDbContext>>();
        await using var database = await factory.CreateDbContextAsync();
        await database.Database.MigrateAsync();
        if (!await database.PortalSettings.AnyAsync())
        {
            database.PortalSettings.Add(new PortalSetting
            {
                Id = PortalSetting.SingletonId,
                Key = PortalSetting.SingletonKey,
                AutoActivateAfterEmailVerification = false,
                UpdatedUtc = Now
            });
            await database.SaveChangesAsync();
        }
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> PostValidRegistrationAsync(
        HttpClient client,
        string email,
        string password)
    {
        using var get = await client.GetAsync("/register");
        var getBody = await get.Content.ReadAsStringAsync();
        var tokenMatch = Regex.Match(
            getBody,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);
        Assert.True(tokenMatch.Success, "The registration page did not contain an antiforgery token.");
        var token = WebUtility.HtmlDecode(tokenMatch.Groups[1].Value);
        using var response = await client.PostAsync("/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = password,
            ["Input.AcceptRules"] = "true",
            ["__RequestVerificationToken"] = token
        }));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string NormalizeDynamicFormValues(string body) => Regex.Replace(
        body,
        "(name=\"__RequestVerificationToken\"[^>]*value=\")[^\"]+(\")",
        "$1<token>$2",
        RegexOptions.CultureInvariant);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }

        return condition();
    }

    private sealed class PortalFactory(string environment, bool trustedProxy = false) : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"mir3-registration-web-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_root);
            var queue = Path.Combine(_root, "queue");
            var locks = Path.Combine(_root, "locks");
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Portal"] = $"Data Source={Path.Combine(_root, "portal.db")}",
                ["VerificationDelivery:LockDirectory"] = locks,
                ["Queue:RootPath"] = queue,
                ["Queue:ResultPollSeconds"] = "2",
                ["MIR3_PUBLIC_BASE_URL"] = "https://portal.example.invalid",
                ["Smtp:Host"] = "smtp.example.invalid",
                ["Smtp:Port"] = "587",
                ["Smtp:UseStartTls"] = "true",
                ["Smtp:FromAddress"] = "mir3@example.invalid",
                ["Smtp:FromName"] = "Mir3",
                ["Smtp:TimeoutSeconds"] = "20",
                ["MIR3_ADMIN_USERNAME"] = "test-bootstrap-admin",
                ["MIR3_ADMIN_INITIAL_PASSWORD"] = "TestBootstrapPass7!"
            }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(
                    trustedProxy ? IPAddress.Parse("192.168.0.64") : IPAddress.Loopback));
                var hosted = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList();
                foreach (var descriptor in hosted) services.Remove(descriptor);
                services.PostConfigure<QueueOptions>(options =>
                    options.HmacKeyBase64 = Convert.ToBase64String(new byte[32]));
                services.PostConfigure<SmtpOptions>(options =>
                {
                    options.Host = "smtp.example.invalid";
                    options.Port = 587;
                    options.UseStartTls = true;
                    options.FromAddress = "mir3@example.invalid";
                    options.FromName = "Mir3";
                    options.TimeoutSeconds = 20;
                    options.Username = "test-user";
                    options.Password = "test-password";
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RemoteIpStartupFilter(IPAddress address) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
            Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware();
            });
            next(app);
        };
    }

    private sealed class ServiceFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly MutableTimeProvider _clock;
        private readonly ContextFactory _factory;
        private readonly EmailVerificationService _email;
        private readonly QueueOptions _queueOptions;

        private ServiceFixture(
            string root,
            PortalDbContext write,
            PortalDbContext read,
            RegistrationService service,
            RegistrationOutboxProcessor outbox,
            RecordingSender sender,
            RecordingLogger<RegistrationService> logs,
            MutableTimeProvider clock,
            ContextFactory factory,
            EmailVerificationService email,
            QueueOptions queueOptions)
        {
            _root = root;
            Write = write;
            Read = read;
            Service = service;
            Outbox = outbox;
            Sender = sender;
            Logs = logs;
            _clock = clock;
            _factory = factory;
            _email = email;
            _queueOptions = queueOptions;
        }

        public PortalDbContext Write { get; }
        public PortalDbContext Read { get; }
        public RegistrationService Service { get; }
        public RegistrationOutboxProcessor Outbox { get; }
        public RecordingSender Sender { get; }
        public RecordingLogger<RegistrationService> Logs { get; }
        public string QueueIncomingPath => Path.Combine(_root, "queue", "incoming");

        public static async Task<ServiceFixture> CreateAsync(
            bool autoActivate = false,
            RegistrationServiceOperations? operations = null,
            AccountQueueClientOperations? queueOperations = null,
            RecordingSender? sender = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"mir3-registration-service-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var dbPath = Path.Combine(root, "portal.db");
            var options = new DbContextOptionsBuilder<PortalDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            var write = new PortalDbContext(options);
            await write.Database.MigrateAsync();
            write.PortalSettings.Add(new PortalSetting
            {
                Id = PortalSetting.SingletonId,
                Key = PortalSetting.SingletonKey,
                AutoActivateAfterEmailVerification = autoActivate,
                UpdatedUtc = Now
            });
            await write.SaveChangesAsync();
            var read = new PortalDbContext(options);
            var factory = new ContextFactory(options);
            var clock = new MutableTimeProvider(Now);
            sender ??= new RecordingSender();
            var email = new EmailVerificationService(
                factory,
                new VerificationTransportLockManager($"Data Source={dbPath}", Path.Combine(root, "locks")),
                new VerificationTokenService(clock),
                sender,
                Microsoft.Extensions.Options.Options.Create(new SmtpOptions
                {
                    PublicBaseUrl = "https://portal.example.invalid",
        Host = "smtp.example.invalid",
                    Port = 587,
                    UseStartTls = true,
                    FromAddress = "mir3@example.invalid",
                    FromName = "Mir3",
                    Username = "user",
                    Password = "password",
                    TimeoutSeconds = 20
                }),
                new RecordingLogger<EmailVerificationService>(),
                clock,
                (_, _) => Task.CompletedTask);
            var queueOptions = new QueueOptions
            {
                RootPath = Path.Combine(root, "queue"),
                ResultPollSeconds = 2,
                HmacKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            };
            var queue = new AccountQueueClient(
                Microsoft.Extensions.Options.Options.Create(queueOptions),
                clock,
                queueOperations ?? AccountQueueClientOperations.Default);
            var outbox = new RegistrationOutboxProcessor(
                factory,
                email,
                queue,
                new RecordingLogger<RegistrationOutboxProcessor>());
            var logs = new RecordingLogger<RegistrationService>();
            var service = new RegistrationService(
                factory,
                new GamePasswordHasher(),
                email,
                new RegistrationStateMachine(),
                queue,
                outbox,
                logs,
                clock,
                operations);
            return new ServiceFixture(root, write, read, service, outbox, sender, logs, clock, factory, email, queueOptions);
        }

        public RegistrationOutboxProcessor CreateRestartedOutbox()
        {
            var queue = new AccountQueueClient(
                Microsoft.Extensions.Options.Options.Create(_queueOptions),
                _clock,
                AccountQueueClientOperations.Default);
            return new RegistrationOutboxProcessor(
                _factory,
                _email,
                queue,
                new RecordingLogger<RegistrationOutboxProcessor>());
        }

        public RegistrationOutboxWorker CreateOutboxWorker() => new(
            Outbox,
            Microsoft.Extensions.Options.Options.Create(new QueueOptions
            {
                RootPath = _queueOptions.RootPath,
                ResultPollSeconds = 1,
                HmacKeyBase64 = _queueOptions.HmacKeyBase64
            }),
            new RecordingLogger<RegistrationOutboxWorker>());

        public async Task<Registration> SeedAsync(string email, RegistrationStatus status)
        {
            var registration = new Registration
            {
                Id = Guid.NewGuid(),
                Email = email,
                NormalizedEmail = email.Trim().ToLowerInvariant(),
                PasswordHash = new GamePasswordHasher().Hash("Secret7"),
                Status = status,
                VerificationTokenHash = new byte[32],
                VerificationExpiresUtc = Now.AddHours(24),
                CreatedUtc = Now,
                UpdatedUtc = Now,
                SourceIpHash = RandomNumberGenerator.GetBytes(32)
            };
            Write.Registrations.Add(registration);
            await Write.SaveChangesAsync();
            return registration;
        }

        public async Task<(Registration Registration, string Token)> SeedWithTokenAsync(bool expired = false, bool used = false)
        {
            var registration = await SeedAsync("player@example.test", RegistrationStatus.PendingEmail);
            var token = new VerificationTokenService(_clock).Issue(registration);
            if (expired) registration.VerificationExpiresUtc = Now.AddSeconds(-1);
            if (used) registration.VerificationUsedUtc = Now.AddMinutes(-1);
            await Write.SaveChangesAsync();
            return (registration, token);
        }

        public async Task<Registration> SeedQueuedAsync(string email)
        {
            var registration = await SeedAsync(email, RegistrationStatus.QueuePending);
            registration.EmailVerifiedUtc = Now.AddMinutes(-1);
            registration.QueueRequestId = Guid.NewGuid();
            registration.QueueCommandType = AccountPortal.Contracts.AccountCommandType.CreateAccount;
            registration.QueuePublicationState = QueuePublicationState.Pending;
            await Write.SaveChangesAsync();
            return registration;
        }

        public async Task<Registration> ReadRegistrationAsync(Guid id)
        {
            Read.ChangeTracker.Clear();
            return await Read.Registrations.SingleAsync(registration => registration.Id == id);
        }

        public async ValueTask DisposeAsync()
        {
            await Read.DisposeAsync();
            await Write.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ContextFactory(DbContextOptions<PortalDbContext> options) : IDbContextFactory<PortalDbContext>
    {
        public PortalDbContext CreateDbContext() => new(options);
        public Task<PortalDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class StubRegistrationService : IRegistrationService
    {
        public RegistrationCreateResult CreateResult { get; init; } = new(
            RegistrationCreateOutcome.Conflict,
            RegistrationMessages.Created,
            Guid.NewGuid());
        public RegistrationVerificationResult VerificationResult { get; init; } = new(
            RegistrationVerificationOutcome.Invalid,
            RegistrationMessages.VerificationInvalid);

        public Task<RegistrationCreateResult> RegisterAsync(string email, string password, IPAddress sourceIp, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateResult);

        public Task<RegistrationVerificationResult> VerifyAsync(string? token, CancellationToken cancellationToken = default) =>
            Task.FromResult(VerificationResult);

        public Task<RegistrationStatusResult> GetStatusAsync(Guid registrationReference, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RegistrationStatusResult(RegistrationStatusOutcome.NotFound, "Unavailable", RegistrationMessages.StatusUnavailable));

        public Task ResendVerificationAsync(Guid registrationReference, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private class RecordingSender : IEmailSender
    {
        public ConcurrentQueue<EmailMessage> Recorded { get; } = new();
        public IReadOnlyList<EmailMessage> Messages => Recorded.ToArray();
        public virtual Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Recorded.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    private sealed class GatedSender : RecordingSender
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await base.SendAsync(message, cancellationToken);
        }
    }

    private sealed class FailingSender : RecordingSender
    {
        public ConcurrentQueue<string> Attempts { get; } = new();
        public IReadOnlyList<string> AttemptedRecipients => Attempts.ToArray();

        public override Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Attempts.Enqueue(message.To.Address);
            throw new SmtpEmailException(SmtpFailureCode.Send, transient: false);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Recorded { get; } = new();
        public IReadOnlyList<string> Messages => Recorded.ToArray();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Recorded.Enqueue(formatter(state, exception));
    }

    private sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }
}
