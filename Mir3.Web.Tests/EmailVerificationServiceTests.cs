using System.Collections.Concurrent;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mir3.Web.Data;
using Mir3.Web.Domain;
using Mir3.Web.Options;
using Mir3.Web.Security;
using Mir3.Web.Services;

namespace Mir3.Web.Tests;

public sealed class EmailVerificationServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Polish_registration_receives_polish_verification_email_even_without_polish_request_context()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        registration.PreferredLanguage = "pl";
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        var sender = new RecordingSender();
        var service = CreateService(database, sender);

        Assert.Equal(EmailVerificationSendOutcome.Sent, (await service.SendAsync(registration.Id)).Outcome);
        var message = Assert.Single(sender.Messages);
        Assert.Contains("Zweryfikuj", message.Subject, StringComparison.Ordinal);
        Assert.Contains("Zweryfikuj rejestrację Mir3", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("Sprawdź status rejestracji", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("Zweryfikuj adres e-mail", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Sprawdź status rejestracji", message.HtmlBody, StringComparison.Ordinal);
        var textLinks = ExtractLinks(message.TextBody).ToArray();
        Assert.Equal(2, textLinks.Length);
        Assert.Equal("https", new Uri(textLinks[0]).Scheme);
        Assert.Equal("portal.example.invalid", new Uri(textLinks[0]).Host);
        Assert.Equal("/verify", new Uri(textLinks[0]).AbsolutePath);
        Assert.Equal("/Status", new Uri(textLinks[1]).AbsolutePath);
        Assert.Equal(registration.Id.ToString("D"), GetQueryValue(new Uri(textLinks[1]), "registration"));
        Assert.Equal(textLinks, ExtractLinks(message.HtmlBody).ToArray());
        Assert.DoesNotContain(Convert.ToHexString(registration.PasswordHash), message.TextBody + message.HtmlBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendAsync_persists_token_before_composing_safe_message_for_normalized_recipient()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        registration.Email = " Player@Example.Test ";
        registration.NormalizedEmail = "player@example.test";
        registration.PasswordHash = Enumerable.Repeat((byte)0xA5, 36).ToArray();
        const string sourceIp = "203.0.113.42";
        registration.SourceIpHash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceIp));
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        var sender = new RecordingSender(async (message, _) =>
        {
            await using var persisted = database.CreateContext();
            var stored = await persisted.Registrations.SingleAsync(item => item.Id == registration.Id);
            Assert.NotEqual(new byte[32], stored.VerificationTokenHash);
            Assert.Null(stored.VerificationUsedUtc);
            Assert.Equal(Now.AddHours(24), stored.VerificationExpiresUtc);
        });
        var logs = new RecordingLogger<EmailVerificationService>();
        var service = CreateService(database, sender, logs);

        var result = await service.SendAsync(registration.Id);

        Assert.Equal(EmailVerificationSendOutcome.Sent, result.Outcome);
        var message = Assert.Single(sender.Messages);
        Assert.Equal("player@example.test", message.To.Address);
        Assert.Null(message.To.Name);
        Assert.Equal("mir3@example.invalid", message.From.Address);
        Assert.Equal("Mir3 Zircon", message.From.Name);
        Assert.Contains("Mir3", message.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verification", message.Subject, StringComparison.OrdinalIgnoreCase);

        var textLinks = ExtractLinks(message.TextBody).ToArray();
        var htmlLinks = ExtractLinks(message.HtmlBody).ToArray();
        Assert.Equal(2, textLinks.Length);
        Assert.Equal(textLinks, htmlLinks);
        var uri = new Uri(textLinks[0], UriKind.Absolute);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("portal.example.invalid", uri.Host);
        Assert.Equal("/verify", uri.AbsolutePath);
        var token = GetQueryValue(uri, "token");
        Assert.NotEmpty(token);
        Assert.DoesNotContain('+', uri.Query);
        Assert.DoesNotContain('/', uri.Query);

        var statusUri = new Uri(textLinks[1], UriKind.Absolute);
        Assert.Equal("https", statusUri.Scheme);
        Assert.Equal("portal.example.invalid", statusUri.Host);
        Assert.Equal("/Status", statusUri.AbsolutePath);
        Assert.Equal(registration.Id.ToString("D"), GetQueryValue(statusUri, "registration"));
        Assert.Contains("Check registration status", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("Check registration status", message.HtmlBody, StringComparison.Ordinal);

        var forbidden = new[]
        {
            Convert.ToHexString(registration.PasswordHash),
            Convert.ToBase64String(registration.PasswordHash),
            Convert.ToHexString(registration.SourceIpHash),
            Convert.ToBase64String(registration.SourceIpHash),
            sourceIp,
            "admin",
            "smtp-user-secret",
            "smtp-password-secret"
        };
        var exposedText = string.Join('\n', new[] { message.Subject, message.TextBody, message.HtmlBody }.Concat(logs.Messages));
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, exposedText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SendAsync_uses_the_validated_bound_sender_identity_when_composing_the_message()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        var sender = new RecordingSender();
        var smtp = CreateSmtpOptions();
        smtp.FromAddress = "verification@example.test";
        smtp.FromName = "Configured Sender";
        var service = CreateService(database, sender, smtpOptions: smtp);

        Assert.Equal(EmailVerificationSendOutcome.Sent, (await service.SendAsync(registration.Id)).Outcome);

        var message = Assert.Single(sender.Messages);
        Assert.Equal("verification@example.test", message.From.Address);
        Assert.Equal("Configured Sender", message.From.Name);
    }

    [Fact]
    public async Task Resend_rotates_persisted_token_and_invalidates_old_link()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        var clock = new MutableTimeProvider(Now);
        var sender = new RecordingSender();
        var service = CreateService(database, sender, timeProvider: clock);

        Assert.Equal(EmailVerificationSendOutcome.Sent, (await service.SendAsync(registration.Id)).Outcome);
        var firstToken = GetQueryValue(new Uri(ExtractLinks(sender.Messages[0].TextBody).First()), "token");
        clock.UtcNow = Now.AddMinutes(10);
        Assert.Equal(EmailVerificationSendOutcome.Sent, (await service.SendAsync(registration.Id)).Outcome);
        var secondToken = GetQueryValue(new Uri(ExtractLinks(sender.Messages[1].TextBody).First()), "token");

        Assert.NotEqual(firstToken, secondToken);
        await using var consumeContext = database.CreateContext();
        var tokens = new VerificationTokenService(clock);
        Assert.False(await tokens.ConsumeAsync(consumeContext, firstToken));
        Assert.True(await tokens.ConsumeAsync(consumeContext, secondToken));
    }

    [Fact]
    public async Task Concurrent_resends_share_one_delivery_and_leave_its_link_valid()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        var sendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new RecordingSender(async (_, cancellationToken) =>
        {
            sendEntered.TrySetResult();
            await releaseSend.Task.WaitAsync(cancellationToken);
        });
        var service = CreateService(
            database,
            sender,
            delay: (duration, cancellationToken) => Task.Delay(duration, cancellationToken));

        var first = service.SendAsync(registration.Id);
        await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.SendAsync(registration.Id);
        await Task.Delay(100);
        Assert.Equal(1, sender.Attempts);

        releaseSend.TrySetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, result => Assert.Equal(EmailVerificationSendOutcome.Sent, result.Outcome));
        var message = Assert.Single(sender.Messages);
        var token = GetQueryValue(new Uri(ExtractLinks(message.TextBody).First()), "token");
        await using var verification = database.CreateContext();
        Assert.True(await new VerificationTokenService(new MutableTimeProvider(Now)).ConsumeAsync(verification, token));
    }

    [Fact]
    public async Task Waiting_resend_retries_after_owner_cancellation_instead_of_reporting_unsent_token_success()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstCancellation = new CancellationTokenSource();
        var attempts = 0;
        var sender = new RecordingSender(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref attempts) != 1)
                return;

            firstEntered.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
            firstCancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        });
        var firstService = CreateService(
            database,
            sender,
            delay: (duration, cancellationToken) => Task.Delay(duration, cancellationToken));
        var waitingService = CreateService(
            database,
            sender,
            delay: (duration, cancellationToken) => Task.Delay(duration, cancellationToken));

        var first = firstService.SendAsync(registration.Id, firstCancellation.Token);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = waitingService.SendAsync(registration.Id);
        releaseFirst.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(EmailVerificationSendOutcome.Sent, result.Outcome);
        Assert.Equal(2, sender.Attempts);
        var messages = sender.Messages;
        Assert.Equal(2, messages.Count);
        var abandonedToken = GetQueryValue(new Uri(ExtractLinks(messages[0].TextBody).First()), "token");
        var deliveredToken = GetQueryValue(new Uri(ExtractLinks(messages[1].TextBody).First()), "token");
        await using var verification = database.CreateContext();
        var tokens = new VerificationTokenService(new MutableTimeProvider(Now));
        Assert.False(await tokens.ConsumeAsync(verification, abandonedToken));
        Assert.True(await tokens.ConsumeAsync(verification, deliveredToken));
    }

    [Fact]
    public async Task Stale_owner_completion_cannot_overwrite_a_newer_successful_attempt()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        var clock = new MutableTimeProvider(Now);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSender = new RecordingSender(async (_, cancellationToken) =>
        {
            firstEntered.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
            throw new SmtpEmailException(SmtpFailureCode.Send, transient: false);
        });
        var firstService = CreateService(database, firstSender, timeProvider: clock);
        var first = firstService.SendAsync(registration.Id);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.UtcNow = Now.AddMinutes(10);
        string currentToken;
        await using (var replacement = database.CreateContext())
        {
            var replacementRegistration = await replacement.Registrations.SingleAsync(item => item.Id == registration.Id);
            currentToken = new VerificationTokenService(clock).Issue(replacementRegistration);
            replacementRegistration.EmailDeliveryAttemptId = Guid.NewGuid();
            replacementRegistration.EmailDeliveryAttemptAcquiredUtc = null;
            replacementRegistration.EmailLastErrorCode = null;
            await replacement.SaveChangesAsync();
        }

        releaseFirst.TrySetResult();
        var stale = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(EmailVerificationSendOutcome.Sent, stale.Outcome);
        Assert.Null(stale.FailureCode);

        await using var verification = database.CreateContext();
        var stored = await verification.Registrations.SingleAsync();
        Assert.Null(stored.EmailLastErrorCode);
        Assert.True(await new VerificationTokenService(clock).ConsumeAsync(verification, currentToken));
    }

    [Fact]
    public async Task Active_sender_that_outlives_the_claim_lease_keeps_exclusive_token_ownership()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        var clock = new MutableTimeProvider(Now);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSender = new RecordingSender(async (_, cancellationToken) =>
        {
            firstEntered.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
        });
        var firstService = CreateService(
            database,
            firstSender,
            timeProvider: clock,
            delay: (duration, cancellationToken) => Task.Delay(duration, cancellationToken));
        var first = firstService.SendAsync(registration.Id);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.UtcNow = Now.AddMinutes(10);
        var secondSender = new RecordingSender();
        var secondService = CreateService(
            database,
            secondSender,
            timeProvider: clock,
            delay: (duration, cancellationToken) => Task.Delay(duration, cancellationToken));
        var second = secondService.SendAsync(registration.Id);
        await Task.Delay(100);

        Assert.Empty(secondSender.Messages);
        releaseFirst.TrySetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, result => Assert.Equal(EmailVerificationSendOutcome.Sent, result.Outcome));
        var message = Assert.Single(firstSender.Messages);
        Assert.Empty(secondSender.Messages);
        var token = GetQueryValue(new Uri(ExtractLinks(message.TextBody).First()), "token");
        await using var verification = database.CreateContext();
        Assert.True(await new VerificationTokenService(clock).ConsumeAsync(verification, token));
    }

    [Fact]
    public async Task Stale_delivery_claim_is_recovered_after_a_crashed_owner()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        var crashedAttemptId = Guid.NewGuid();
        await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Registrations"
            SET "EmailDeliveryAttemptId" = {crashedAttemptId},
                "EmailDeliveryAttemptAcquiredUtc" = {Now.AddMinutes(-10)}
            WHERE "Id" = {registration.Id}
            """);
        var sender = new RecordingSender();
        var service = CreateService(database, sender);

        var result = await service.SendAsync(registration.Id);

        Assert.Equal(EmailVerificationSendOutcome.Sent, result.Outcome);
        Assert.Single(sender.Messages);
        await using var verification = database.CreateContext();
        var currentAttemptId = await verification.Registrations
            .Where(item => item.Id == registration.Id)
            .Select(item => EF.Property<Guid?>(item, "EmailDeliveryAttemptId"))
            .SingleAsync();
        Assert.NotNull(currentAttemptId);
        Assert.NotEqual(crashedAttemptId, currentAttemptId);
    }

    [Fact]
    public async Task Transient_connect_failure_retries_at_most_three_attempts_with_exact_delays_and_persists_safe_code()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        var sender = new RecordingSender((_, _) => throw new SmtpEmailException(SmtpFailureCode.Connect, transient: true));
        var delays = new List<TimeSpan>();
        var logs = new RecordingLogger<EmailVerificationService>();
        var service = CreateService(database, sender, logs, delay: (duration, _) =>
        {
            delays.Add(duration);
            return Task.CompletedTask;
        });

        var result = await service.SendAsync(registration.Id);

        Assert.Equal(EmailVerificationSendOutcome.Failed, result.Outcome);
        Assert.Equal("smtp-connect", result.FailureCode);
        Assert.Equal(3, sender.Attempts);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)], delays);
        await using var verification = database.CreateContext();
        var stored = await verification.Registrations.SingleAsync();
        Assert.Equal(RegistrationStatus.PendingEmail, stored.Status);
        Assert.Equal("smtp-connect", stored.EmailLastErrorCode);
        Assert.DoesNotContain(logs.Messages, message => message.Contains("server detail", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Authentication_failure_is_not_retried_and_only_safe_code_is_persisted()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        var sender = new RecordingSender((_, _) => throw new SmtpEmailException(SmtpFailureCode.Authentication, transient: false));
        var delays = new List<TimeSpan>();
        var service = CreateService(database, sender, delay: (duration, _) =>
        {
            delays.Add(duration);
            return Task.CompletedTask;
        });

        var result = await service.SendAsync(registration.Id);

        Assert.Equal(EmailVerificationSendOutcome.Failed, result.Outcome);
        Assert.Equal("smtp-auth", result.FailureCode);
        Assert.Equal(1, sender.Attempts);
        Assert.Empty(delays);
        await using var verification = database.CreateContext();
        Assert.Equal("smtp-auth", (await verification.Registrations.SingleAsync()).EmailLastErrorCode);
    }

    [Fact]
    public async Task Unknown_sender_failure_is_not_retried_and_is_persisted_as_safe_send_code()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        var sender = new RecordingSender((_, _) => throw new InvalidOperationException("raw programming detail"));
        var delays = new List<TimeSpan>();
        var service = CreateService(database, sender, delay: (duration, _) =>
        {
            delays.Add(duration);
            return Task.CompletedTask;
        });

        var result = await service.SendAsync(registration.Id);

        Assert.Equal(EmailVerificationSendOutcome.Failed, result.Outcome);
        Assert.Equal("smtp-send", result.FailureCode);
        Assert.Equal(1, sender.Attempts);
        Assert.Empty(delays);
        await using var verification = database.CreateContext();
        Assert.Equal("smtp-send", (await verification.Registrations.SingleAsync()).EmailLastErrorCode);
    }

    [Fact]
    public async Task Successful_resend_clears_previous_safe_mail_failure()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        registration.EmailLastErrorCode = "smtp-send";
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        var service = CreateService(database, new RecordingSender());

        Assert.Equal(EmailVerificationSendOutcome.Sent, (await service.SendAsync(registration.Id)).Outcome);

        await using var verification = database.CreateContext();
        Assert.Null((await verification.Registrations.SingleAsync()).EmailLastErrorCode);
    }

    [Fact]
    public async Task Resend_after_final_failure_rotates_the_still_valid_failed_delivery_token()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        var attempts = 0;
        var sender = new RecordingSender((_, _) =>
        {
            attempts++;
            return attempts <= 3
                ? Task.FromException(new SmtpEmailException(SmtpFailureCode.Send, transient: true))
                : Task.CompletedTask;
        });
        var service = CreateService(database, sender);

        var failedResult = await service.SendAsync(registration.Id);
        Assert.Equal(EmailVerificationSendOutcome.Failed, failedResult.Outcome);
        Assert.Equal("smtp-send", failedResult.FailureCode);
        var failedToken = GetQueryValue(new Uri(ExtractLinks(sender.Messages[0].TextBody).First()), "token");

        await using (var failedDelivery = database.CreateContext())
        {
            var stored = await failedDelivery.Registrations.SingleAsync();
            Assert.Equal(RegistrationStatus.PendingEmail, stored.Status);
            Assert.Equal("smtp-send", stored.EmailLastErrorCode);
        }

        Assert.Equal(EmailVerificationSendOutcome.Sent, (await service.SendAsync(registration.Id)).Outcome);
        var resentToken = GetQueryValue(new Uri(ExtractLinks(sender.Messages[3].TextBody).First()), "token");

        Assert.NotEqual(failedToken, resentToken);
        await using var consumeContext = database.CreateContext();
        var tokens = new VerificationTokenService(new MutableTimeProvider(Now));
        Assert.False(await tokens.ConsumeAsync(consumeContext, failedToken));
        Assert.True(await tokens.ConsumeAsync(consumeContext, resentToken));
        consumeContext.ChangeTracker.Clear();
        Assert.Null((await consumeContext.Registrations.SingleAsync()).EmailLastErrorCode);
    }

    [Fact]
    public async Task Token_persistence_failure_sends_nothing_and_allows_retry()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Registrations"
            SET "EmailDeliveryAttemptId" = {Guid.NewGuid()},
                "EmailDeliveryAttemptAcquiredUtc" = {Now.AddMinutes(-10)}
            WHERE "Id" = {registration.Id}
            """);
        var sender = new RecordingSender();
        var factory = new InterceptingFactory(database.Path, new FailFirstSaveInterceptor());
        var service = new EmailVerificationService(
            factory,
            database.TransportLocks,
            new VerificationTokenService(new MutableTimeProvider(Now)),
            sender,
            Microsoft.Extensions.Options.Options.Create(CreateSmtpOptions()),
            new RecordingLogger<EmailVerificationService>(),
            new MutableTimeProvider(Now),
            (duration, cancellationToken) => Task.Delay(duration, cancellationToken));

        await Assert.ThrowsAsync<DbUpdateException>(() => service.SendAsync(registration.Id));
        Assert.Equal(0, sender.Attempts);
        Assert.Equal(
            EmailVerificationSendOutcome.Sent,
            (await service.SendAsync(registration.Id).WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        Assert.Equal(1, sender.Attempts);
    }

    [Fact]
    public async Task Cancellation_is_not_retried_or_mapped_to_an_smtp_failure_and_allows_later_resend()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        var cancelledSender = new RecordingSender((_, cancellationToken) =>
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        var service = CreateService(database, cancelledSender);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SendAsync(registration.Id, cancellation.Token));
        Assert.Equal(1, cancelledSender.Attempts);

        await using (var cancelledDelivery = database.CreateContext())
        {
            var stored = await cancelledDelivery.Registrations.SingleAsync();
            Assert.Equal(RegistrationStatus.PendingEmail, stored.Status);
            Assert.Null(stored.EmailLastErrorCode);
        }

        var retrySender = new RecordingSender();
        var retryService = CreateService(database, retrySender);
        Assert.Equal(EmailVerificationSendOutcome.Sent, (await retryService.SendAsync(registration.Id)).Outcome);
        Assert.Single(retrySender.Messages);
    }

    [Fact]
    public async Task SendAsync_retries_real_sqlite_write_contention_and_recovers_when_lock_is_released()
    {
        await using var database = await TestDatabase.CreateMigratedAsync(defaultTimeoutSeconds: 0);
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        await using var blocker = new SqliteConnection($"Data Source={database.Path};Default Timeout=0");
        await blocker.OpenAsync();
        await using (var command = blocker.CreateCommand())
        {
            command.CommandText = "BEGIN EXCLUSIVE; UPDATE Registrations SET UpdatedUtc = UpdatedUtc;";
            await command.ExecuteNonQueryAsync();
        }

        var sender = new RecordingSender();
        var send = CreateService(database, sender).SendAsync(registration.Id);
        await Task.Delay(150);
        await using (var release = blocker.CreateCommand())
        {
            release.CommandText = "ROLLBACK;";
            await release.ExecuteNonQueryAsync();
        }

        var result = await send.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(EmailVerificationSendOutcome.Sent, result.Outcome);
        Assert.Single(sender.Messages);
    }

    [Fact]
    public async Task SendAsync_maps_persistent_sqlite_write_contention_to_a_bounded_controlled_result()
    {
        await using var database = await TestDatabase.CreateMigratedAsync(defaultTimeoutSeconds: 0);
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        await using var blocker = new SqliteConnection($"Data Source={database.Path};Default Timeout=0");
        await blocker.OpenAsync();
        await using (var command = blocker.CreateCommand())
        {
            command.CommandText = "BEGIN EXCLUSIVE; UPDATE Registrations SET UpdatedUtc = UpdatedUtc;";
            await command.ExecuteNonQueryAsync();
        }

        var result = await CreateService(database, new RecordingSender())
            .SendAsync(registration.Id)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(EmailVerificationSendOutcome.Failed, result.Outcome);
        Assert.Equal("smtp-send", result.FailureCode);
    }

    [Fact]
    public async Task Cancellation_racing_with_real_sqlite_contention_propagates_and_releases_the_claim()
    {
        await using var database = await TestDatabase.CreateMigratedAsync(defaultTimeoutSeconds: 0);
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        await using var blocker = new SqliteConnection($"Data Source={database.Path};Default Timeout=0");
        await blocker.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var lockAcquired = false;
        var interceptor = new CancelAndReleaseOnContentionInterceptor(cancellation, blocker);
        var factory = new InterceptingFactory(database.Path, interceptor, defaultTimeoutSeconds: 0);
        var service = CreateService(
            factory,
            database.TransportLocks,
            new RecordingSender(),
            new EmailVerificationServiceOperations
            {
                BeforeBeginDeliveryTransport = async _ =>
                {
                    if (lockAcquired) return;
                    lockAcquired = true;
                    await using var command = blocker.CreateCommand();
                    command.CommandText = "BEGIN IMMEDIATE; UPDATE Registrations SET UpdatedUtc = UpdatedUtc;";
                    await command.ExecuteNonQueryAsync();
                }
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SendAsync(registration.Id, cancellation.Token));

        Assert.True(interceptor.ObservedContention);
        await using var verification = database.CreateContext();
        var stored = await verification.Registrations.SingleAsync();
        Assert.Null(stored.EmailDeliveryAttemptId);
        Assert.Null(stored.EmailDeliveryAttemptAcquiredUtc);
    }

    [Fact]
    public async Task Transport_lock_timeout_returns_safe_failure_releases_claim_and_allows_retry()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        await using var blocker = Assert.IsType<VerificationTransportLock>(
            database.TransportLocks.TryAcquire(registration.Id));
        var clock = new MutableTimeProvider(Now);
        var sender = new RecordingSender();
        var service = CreateService(
            database,
            sender,
            timeProvider: clock,
            delay: async (duration, _) =>
            {
                clock.Advance(duration);
                await Task.Yield();
            });

        var result = await service.SendAsync(registration.Id, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(EmailVerificationSendOutcome.Failed, result.Outcome);
        Assert.Equal("smtp-send", result.FailureCode);
        Assert.Equal(0, sender.Attempts);
        await using (var verification = database.CreateContext())
        {
            var stored = await verification.Registrations.SingleAsync();
            Assert.Null(stored.EmailDeliveryAttemptId);
            Assert.Null(stored.EmailDeliveryAttemptAcquiredUtc);
            Assert.Equal("smtp-send", stored.EmailLastErrorCode);
        }

        await blocker.DisposeAsync();
        var retrySender = new RecordingSender();
        var retry = await CreateService(database, retrySender)
            .SendAsync(registration.Id)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(EmailVerificationSendOutcome.Sent, retry.Outcome);
        Assert.Single(retrySender.Messages);
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_transport_lock_releases_the_committed_claim()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        await using var blocker = Assert.IsType<VerificationTransportLock>(
            database.TransportLocks.TryAcquire(registration.Id));
        using var cancellation = new CancellationTokenSource();

        var cancelled = CreateService(
            database,
            new RecordingSender(),
            delay: (duration, token) => Task.Delay(duration, token))
            .SendAsync(registration.Id, cancellation.Token);
        await WaitForClaimAsync(database, registration.Id);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        await blocker.DisposeAsync();

        var retrySender = new RecordingSender();
        var result = await CreateService(database, retrySender)
            .SendAsync(registration.Id)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(EmailVerificationSendOutcome.Sent, result.Outcome);
        Assert.Single(retrySender.Messages);
    }

    [Fact]
    public async Task Cancellation_during_begin_transport_persistence_releases_the_committed_claim()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var registration = CreateRegistration();
        database.Context.Registrations.Add(registration);
        await database.Context.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        var service = CreateService(
            database,
            database.TransportLocks,
            new RecordingSender(),
            new EmailVerificationServiceOperations
            {
                BeforeBeginDeliveryTransport = token =>
                {
                    cancellation.Cancel();
                    return Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SendAsync(registration.Id, cancellation.Token));

        var retrySender = new RecordingSender();
        var result = await CreateService(database, retrySender)
            .SendAsync(registration.Id)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(EmailVerificationSendOutcome.Sent, result.Outcome);
        Assert.Single(retrySender.Messages);
    }

    [Fact]
    public void Smtp_options_read_credentials_only_from_environment_and_validate_without_leaking_values()
    {
        const string username = "smtp-user-secret";
        const string password = "smtp-password-secret";
        var values = new Dictionary<string, string?>
        {
            ["MIR3_PUBLIC_BASE_URL"] = "https://portal.example.invalid",
                ["Smtp:Host"] = "smtp.example.invalid",
            ["Smtp:Port"] = "587",
            ["Smtp:UseStartTls"] = "true",
            ["Smtp:FromAddress"] = "mir3@example.invalid",
            ["Smtp:FromName"] = "Mir3 Zircon",
            ["Smtp:TimeoutSeconds"] = "20",
            ["Smtp:Username"] = "committed-user-must-be-ignored",
            ["Smtp:Password"] = "committed-password-must-be-ignored"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new SmtpOptions();
        new SmtpOptionsSetup(configuration, name => name switch
        {
            SmtpOptions.UsernameEnvironmentVariable => username,
            SmtpOptions.PasswordEnvironmentVariable => password,
            _ => null
        }).Configure(options);

        Assert.Equal(username, options.Username);
        Assert.Equal(password, options.Password);
        Assert.Equal("smtp.example.invalid", options.Host);
        Assert.Equal(587, options.Port);
        Assert.True(options.UseStartTls);
        Assert.Equal("mir3@example.invalid", options.FromAddress);
        Assert.Equal("Mir3 Zircon", options.FromName);
        Assert.Equal(20, options.TimeoutSeconds);
        Assert.Same(
            ValidateOptionsResult.Success,
            new SmtpOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, options));

        options.Password = null;
        var failure = new SmtpOptionsValidator().Validate(
            Microsoft.Extensions.Options.Options.DefaultName,
            options);
        Assert.True(failure.Failed);
        var error = string.Join('\n', failure.Failures);
        Assert.DoesNotContain(username, error, StringComparison.Ordinal);
        Assert.DoesNotContain(password, error, StringComparison.Ordinal);
        Assert.DoesNotContain("/", error, StringComparison.Ordinal);
        Assert.Contains(SmtpOptions.PasswordEnvironmentVariable, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mir3@example.test")]
    [InlineData("\"quoted@local\"@example.test")]
    public void Smtp_options_accept_exactly_one_syntactically_valid_address_only_mailbox(string fromAddress)
    {
        var options = CreateSmtpOptions();
        options.FromAddress = fromAddress;

        var result = new SmtpOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, options);

        Assert.Same(ValidateOptionsResult.Success, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("first@example.test, second@example.test")]
    [InlineData("Display Name <sender@example.test>")]
    [InlineData("sender@example.test\r\nBcc: victim@example.test")]
    [InlineData(" sender@example.test ")]
    public void Smtp_options_reject_invalid_or_non_address_only_sender_mailbox(string fromAddress)
    {
        var options = CreateSmtpOptions();
        options.FromAddress = fromAddress;

        var result = new SmtpOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, options);

        Assert.True(result.Failed);
        if (fromAddress.Length > 0)
            Assert.DoesNotContain(fromAddress, string.Join('\n', result.Failures), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Mir3\r\nBcc: victim@example.test")]
    [InlineData("Mir3\u0000Zircon")]
    [InlineData("Mir3\u001fZircon")]
    [InlineData("Mir3\u007fZircon")]
    public void Smtp_options_reject_control_characters_in_sender_name(string fromName)
    {
        var options = CreateSmtpOptions();
        options.FromName = fromName;

        var result = new SmtpOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.DoesNotContain(fromName, string.Join('\n', result.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Committed_smtp_sender_identity_matches_the_portal_specification()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        Assert.Equal("mir3@example.invalid", configuration["Smtp:FromAddress"]);
        Assert.Equal("Mir3 Zircon", configuration["Smtp:FromName"]);
    }

    [Fact]
    public void Committed_verification_storage_defaults_use_absolute_private_service_paths()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        Assert.Equal("Data Source=/var/lib/mir3-web/portal.db", configuration.GetConnectionString("Portal"));
        Assert.Equal("/var/lib/mir3-web/verification-locks", configuration["VerificationDelivery:LockDirectory"]);
    }

    private static EmailVerificationService CreateService(
        TestDatabase database,
        IEmailSender sender,
        RecordingLogger<EmailVerificationService>? logger = null,
        MutableTimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        SmtpOptions? smtpOptions = null) =>
        new(
            database,
            database.TransportLocks,
            new VerificationTokenService(timeProvider ?? new MutableTimeProvider(Now)),
            sender,
            Microsoft.Extensions.Options.Options.Create(smtpOptions ?? CreateSmtpOptions()),
            logger ?? new RecordingLogger<EmailVerificationService>(),
            timeProvider ?? new MutableTimeProvider(Now),
            delay ?? ((_, _) => Task.CompletedTask));

    private static EmailVerificationService CreateService(
        IDbContextFactory<PortalDbContext> factory,
        VerificationTransportLockManager transportLocks,
        IEmailSender sender,
        EmailVerificationServiceOperations? operations = null) =>
        new(
            factory,
            transportLocks,
            new VerificationTokenService(new MutableTimeProvider(Now)),
            sender,
            Microsoft.Extensions.Options.Options.Create(CreateSmtpOptions()),
            new RecordingLogger<EmailVerificationService>(),
            new MutableTimeProvider(Now),
            (_, _) => Task.CompletedTask,
            operations);

    private static async Task WaitForClaimAsync(TestDatabase database, Guid registrationId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var context = database.CreateContext();
            var claimed = await context.Registrations
                .Where(item => item.Id == registrationId)
                .Select(item => item.EmailDeliveryAttemptAcquiredUtc)
                .SingleAsync();
            if (claimed is not null) return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException("The delivery claim was not acquired.");
    }

    private static SmtpOptions CreateSmtpOptions() => new()
    {
        PublicBaseUrl = "https://portal.example.invalid",
        Host = "smtp.example.invalid",
        Port = 587,
        UseStartTls = true,
        FromAddress = "mir3@example.invalid",
        FromName = "Mir3 Zircon",
        TimeoutSeconds = 20,
        Username = "smtp-user-secret",
        Password = "smtp-password-secret"
    };

    private static Registration CreateRegistration() => new()
    {
        Id = Guid.NewGuid(),
        Email = "Player@Example.Test",
        NormalizedEmail = "player@example.test",
        PasswordHash = RandomNumberGenerator.GetBytes(36),
        Status = RegistrationStatus.PendingEmail,
        VerificationTokenHash = new byte[32],
        VerificationExpiresUtc = Now,
        CreatedUtc = Now,
        UpdatedUtc = Now,
        SourceIpHash = RandomNumberGenerator.GetBytes(32)
    };

    private static IEnumerable<string> ExtractLinks(string value)
    {
        const string prefix = "https://portal.example.invalid/verify?token=";
        var index = 0;
        while ((index = value.IndexOf(prefix, index, StringComparison.Ordinal)) >= 0)
        {
            var end = index;
            while (end < value.Length && value[end] is not (' ' or '\r' or '\n' or '"' or '<')) end++;
            yield return value[index..end].Replace("&amp;", "&", StringComparison.Ordinal);
            index = end;
        }

        const string statusPrefix = "https://portal.example.invalid/Status?registration=";
        index = 0;
        while ((index = value.IndexOf(statusPrefix, index, StringComparison.Ordinal)) >= 0)
        {
            var end = index;
            while (end < value.Length && value[end] is not (' ' or '\r' or '\n' or '"' or '<')) end++;
            yield return value[index..end].Replace("&amp;", "&", StringComparison.Ordinal);
            index = end;
        }
    }

    private static string GetQueryValue(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]) == key)
            {
                return Uri.UnescapeDataString(parts.Length == 2 ? parts[1] : string.Empty);
            }
        }

        throw new Xunit.Sdk.XunitException($"Query parameter '{key}' was absent.");
    }

    private sealed class RecordingSender(Func<EmailMessage, CancellationToken, Task>? send = null) : IEmailSender
    {
        private readonly Func<EmailMessage, CancellationToken, Task> _send = send ?? ((_, _) => Task.CompletedTask);
        private int _attempts;

        public ConcurrentQueue<EmailMessage> RecordedMessages { get; } = new();

        public IReadOnlyList<EmailMessage> Messages => [.. RecordedMessages];

        public int Attempts => _attempts;

        public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            RecordedMessages.Enqueue(message);
            await _send(message, cancellationToken);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> RecordedMessages { get; } = new();

        public IReadOnlyList<string> Messages => [.. RecordedMessages];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            RecordedMessages.Enqueue(formatter(state, exception));
    }

    private sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
    {
        private long _timestamp;

        public DateTime UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => new(UtcNow);

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan duration)
        {
            UtcNow += duration;
            _timestamp += duration.Ticks;
        }
    }

    private sealed class TestDatabase : IDbContextFactory<PortalDbContext>, IAsyncDisposable
    {
        public string Path { get; }

        public PortalDbContext Context { get; }

        public VerificationTransportLockManager TransportLocks { get; }

        public PortalDbContext CreateContext() => new(CreateOptions(Path, _defaultTimeoutSeconds));

        public PortalDbContext CreateDbContext() => CreateContext();

        public Task<PortalDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateContext());

        public static async Task<TestDatabase> CreateMigratedAsync(int defaultTimeoutSeconds = 30)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mir3-email-tests-{Guid.NewGuid():N}.db");
            var context = new PortalDbContext(CreateOptions(path, defaultTimeoutSeconds));
            await context.Database.MigrateAsync();
            return new TestDatabase(path, context, defaultTimeoutSeconds);
        }

        private readonly int _defaultTimeoutSeconds;

        private TestDatabase(string path, PortalDbContext context, int defaultTimeoutSeconds)
        {
            Path = path;
            Context = context;
            _defaultTimeoutSeconds = defaultTimeoutSeconds;
            TransportLocks = new VerificationTransportLockManager(
                $"Data Source={path}",
                path + ".verification-locks");
        }

        private static DbContextOptions<PortalDbContext> CreateOptions(string path, int defaultTimeoutSeconds) =>
            new DbContextOptionsBuilder<PortalDbContext>()
                .UseSqlite($"Data Source={path};Default Timeout={defaultTimeoutSeconds}")
                .Options;

        public async ValueTask DisposeAsync()
        {
            TransportLocks.Dispose();
            await Context.DisposeAsync();
            File.Delete(Path);
            if (Directory.Exists(Path + ".verification-locks"))
                Directory.Delete(Path + ".verification-locks", recursive: true);
        }
    }

    private sealed class InterceptingFactory(
        string path,
        IInterceptor interceptor,
        int defaultTimeoutSeconds = 30)
        : IDbContextFactory<PortalDbContext>
    {
        public PortalDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<PortalDbContext>()
                .UseSqlite($"Data Source={path};Default Timeout={defaultTimeoutSeconds}")
                .AddInterceptors(interceptor)
                .Options);

        public Task<PortalDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FailFirstSaveInterceptor : SaveChangesInterceptor
    {
        private int _remainingFailures = 1;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _remainingFailures, 0) == 1)
                throw new DbUpdateException("forced persistence failure");

            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancelAndReleaseOnContentionInterceptor(
        CancellationTokenSource cancellation,
        SqliteConnection blocker) : DbCommandInterceptor
    {
        private int _released;

        public bool ObservedContention { get; private set; }

        public override async Task CommandFailedAsync(
            System.Data.Common.DbCommand command,
            CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is not SqliteException { SqliteErrorCode: 5 or 6 }) return;

            ObservedContention = true;
            cancellation.Cancel();
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                await using var release = blocker.CreateCommand();
                release.CommandText = "ROLLBACK;";
                await release.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }

}
