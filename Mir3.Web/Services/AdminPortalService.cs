using System.Text.Json;
using AccountPortal.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mir3.Web.Data;
using Mir3.Web.Domain;
using Mir3.Web.Security;

namespace Mir3.Web.Services;

public sealed record AdminLoginResult(bool Succeeded, string? Username, string Message)
{
    public static AdminLoginResult Success(string username) => new(true, username, string.Empty);

    public static AdminLoginResult Failure { get; } =
        new(false, null, "The username or password is incorrect.");
}

public sealed record AdminRegistrationRow(
    Guid Id,
    string Email,
    RegistrationStatus Status,
    string StatusLabel,
    string BadgeClass,
    string BadgeIcon,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record AdminAuditRow(
    string Actor,
    string Action,
    string Target,
    DateTime CreatedUtc);

public sealed record AdminDashboard(
    IReadOnlyList<AdminRegistrationRow> Registrations,
    IReadOnlyList<AdminAuditRow> AuditEntries);

public sealed record AdminActionResult(bool Succeeded, string Message);

public sealed class AdminPortalService(
    IDbContextFactory<PortalDbContext> dbContextFactory,
    IPasswordHasher<AdminUser> passwordHasher,
    RegistrationStateMachine stateMachine,
    RegistrationOutboxProcessor outbox,
    GamePasswordHasher gamePasswordHasher,
    TimeProvider timeProvider,
    ILogger<AdminPortalService> logger)
{
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public async Task<AdminLoginResult> AuthenticateAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || password is null)
        {
            return AdminLoginResult.Failure;
        }

        var normalizedUsername = username.Trim().ToUpperInvariant();
        await using var database = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var admin = await database.AdminUsers
            .SingleOrDefaultAsync(item => item.NormalizedUsername == normalizedUsername, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (admin is null || admin.LockoutUntilUtc is { } lockoutUntil && lockoutUntil > now)
        {
            return AdminLoginResult.Failure;
        }

        PasswordVerificationResult verification;
        try
        {
            verification = passwordHasher.VerifyHashedPassword(admin, admin.PasswordHash, password);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            verification = PasswordVerificationResult.Failed;
        }

        if (verification is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded)
        {
            admin.FailedAttempts = 0;
            admin.LockoutUntilUtc = null;
            admin.UpdatedUtc = now;
            if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            {
                admin.PasswordHash = passwordHasher.HashPassword(admin, password);
            }

            await database.SaveChangesAsync(cancellationToken);
            return AdminLoginResult.Success(admin.Username);
        }

        await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "AdminUsers"
            SET "FailedAttempts" = "FailedAttempts" + 1,
                "LockoutUntilUtc" = CASE
                    WHEN "FailedAttempts" + 1 >= 5 THEN {now.Add(LockoutDuration)}
                    ELSE "LockoutUntilUtc"
                END,
                "UpdatedUtc" = {now}
            WHERE "Id" = {admin.Id}
            """, cancellationToken);
        return AdminLoginResult.Failure;
    }

    public async Task<AdminDashboard> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        await using var database = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var registrations = (await database.Registrations
            .AsNoTracking()
            .OrderByDescending(item => item.UpdatedUtc)
            .Select(item => new AdminRegistrationRow(
                item.Id,
                item.Email,
                item.Status,
                string.Empty,
                string.Empty,
                string.Empty,
                item.CreatedUtc,
                item.UpdatedUtc))
            .ToListAsync(cancellationToken))
            .Select(item => item with
            {
                StatusLabel = StatusLabel(item.Status),
                BadgeClass = BadgeClass(item.Status),
                BadgeIcon = BadgeIcon(item.Status)
            })
            .ToList();
        var auditEntries = await database.AuditEntries
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedUtc)
            .ThenByDescending(item => item.Id)
            .Take(100)
            .Select(item => new AdminAuditRow(item.Actor, item.Action, item.Target, item.CreatedUtc))
            .ToListAsync(cancellationToken);

        return new AdminDashboard(registrations, auditEntries);
    }

    public async Task<AdminActionResult> ApplyRegistrationActionAsync(
        Guid registrationId,
        bool activate,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (registrationId == Guid.Empty)
        {
            return new AdminActionResult(false, "The registration could not be found.");
        }

        await using var database = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await database.Database.OpenConnectionAsync(cancellationToken);
        if (database.Database.GetDbConnection() is not SqliteConnection connection)
        {
            throw new InvalidOperationException("The portal database must use SQLite.");
        }

        await using var transaction = connection.BeginTransaction(deferred: false);
        database.Database.UseTransaction(transaction);
        var registration = await database.Registrations
            .SingleOrDefaultAsync(item => item.Id == registrationId, cancellationToken);
        if (registration is null)
        {
            return new AdminActionResult(false, "The registration could not be found.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var requestId = Guid.NewGuid();
        AccountPortal.Contracts.AccountCommandType commandType;
        try
        {
            commandType = activate
                ? stateMachine.AdminActivate(registration, requestId, now)
                : stateMachine.AdminDeactivate(registration, requestId, now);
        }
        catch (RegistrationTransitionException)
        {
            var action = activate ? "activated" : "deactivated";
            return new AdminActionResult(false, $"This registration cannot be {action} in its current state.");
        }

        database.AuditEntries.Add(new AuditEntry
        {
            Actor = actor,
            Action = activate ? "Admin.Registration.Activate" : "Admin.Registration.Deactivate",
            Target = $"registration:{registration.Id:D}",
            DetailsJson = JsonSerializer.Serialize(new { command = commandType.ToString() }),
            CreatedUtc = now
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);

        try
        {
            await outbox.PublishQueueAsync(registration.Id, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Admin account command publication kick failed for registration {RegistrationId}; durable outbox recovery remains pending.",
                registration.Id);
        }

        return new AdminActionResult(true, activate
            ? "Account activation queued."
            : "Account deactivation queued.");
    }

    public async Task<AdminActionResult> ResetPasswordAsync(
        Guid registrationId,
        string? newPassword,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (registrationId == Guid.Empty)
        {
            return new AdminActionResult(false, "The registration could not be found.");
        }

        byte[] replacementHash;
        try
        {
            replacementHash = gamePasswordHasher.Hash(newPassword!);
        }
        catch (ArgumentException)
        {
            return new AdminActionResult(
                false,
                $"Password must be between {GamePasswordHasher.MinimumPasswordLength} and {GamePasswordHasher.MaximumPasswordLength} characters, with no whitespace.");
        }

        await using var database = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await database.Database.OpenConnectionAsync(cancellationToken);
        if (database.Database.GetDbConnection() is not SqliteConnection connection)
        {
            throw new InvalidOperationException("The portal database must use SQLite.");
        }

        await using var transaction = connection.BeginTransaction(deferred: false);
        database.Database.UseTransaction(transaction);
        var registration = await database.Registrations
            .SingleOrDefaultAsync(item => item.Id == registrationId, cancellationToken);
        if (registration is null)
        {
            return new AdminActionResult(false, "The registration could not be found.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AccountCommandType commandType;
        try
        {
            commandType = stateMachine.AdminResetPassword(
                registration,
                replacementHash,
                Guid.NewGuid(),
                now);
        }
        catch (RegistrationTransitionException)
        {
            return new AdminActionResult(false, "This registration cannot have its password reset in its current state.");
        }

        database.AuditEntries.Add(new AuditEntry
        {
            Actor = actor,
            Action = "Admin.Registration.ResetPassword",
            Target = $"registration:{registration.Id:D}",
            DetailsJson = JsonSerializer.Serialize(new { command = commandType.ToString() }),
            CreatedUtc = now
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);

        try
        {
            await outbox.PublishQueueAsync(registration.Id, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Admin password reset publication kick failed for registration {RegistrationId}; durable outbox recovery remains pending.",
                registration.Id);
        }

        return new AdminActionResult(true, "Password reset queued. The plaintext password is never stored or shown by the portal.");
    }

    public async Task<(bool Succeeded, string Message)> UpdateSettingsAsync(
        bool autoActivate,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await using var database = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var setting = await database.PortalSettings
            .SingleOrDefaultAsync(item => item.Id == PortalSetting.SingletonId, cancellationToken);
        if (setting is null)
        {
            return (false, "Portal settings are unavailable.");
        }

        var oldValue = setting.AutoActivateAfterEmailVerification;
        if (oldValue != autoActivate)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            setting.AutoActivateAfterEmailVerification = autoActivate;
            setting.UpdatedUtc = now;
            database.AuditEntries.Add(new AuditEntry
            {
                Actor = actor,
                Action = "Admin.Settings.AutoActivate",
                Target = PortalSetting.SingletonKey,
                DetailsJson = JsonSerializer.Serialize(new { old = oldValue, @new = autoActivate }),
                CreatedUtc = now
            });
            await database.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, "Settings saved.");
    }

    public async Task<bool> GetAutoActivateAsync(CancellationToken cancellationToken = default)
    {
        await using var database = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await database.PortalSettings
            .Where(item => item.Id == PortalSetting.SingletonId)
            .Select(item => item.AutoActivateAfterEmailVerification)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static string StatusLabel(RegistrationStatus status) => status switch
    {
        RegistrationStatus.PendingEmail => "Pending e-mail",
        RegistrationStatus.AwaitingAdmin => "Awaiting admin",
        RegistrationStatus.QueuePending => "Queue pending",
        RegistrationStatus.Active => "Active",
        RegistrationStatus.Disabled => "Disabled",
        RegistrationStatus.Failed => "Failed",
        _ => "Unknown"
    };

    private static string BadgeClass(RegistrationStatus status) => status switch
    {
        RegistrationStatus.PendingEmail => "status-badge status-pending-email",
        RegistrationStatus.AwaitingAdmin => "status-badge status-awaiting-admin",
        RegistrationStatus.QueuePending => "status-badge status-queue-pending",
        RegistrationStatus.Active => "status-badge status-active",
        RegistrationStatus.Disabled => "status-badge status-disabled",
        RegistrationStatus.Failed => "status-badge status-failed",
        _ => "status-badge"
    };

    private static string BadgeIcon(RegistrationStatus status) => status switch
    {
        RegistrationStatus.PendingEmail => "◷",
        RegistrationStatus.AwaitingAdmin => "⬟",
        RegistrationStatus.QueuePending => "◌",
        RegistrationStatus.Active => "✓",
        RegistrationStatus.Disabled => "Ⅱ",
        RegistrationStatus.Failed => "⚠",
        _ => "?"
    };
}
