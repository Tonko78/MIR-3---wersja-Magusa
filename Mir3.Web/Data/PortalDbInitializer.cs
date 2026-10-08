using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Mir3.Web.Domain;

namespace Mir3.Web.Data;

public static class PortalDbInitializer
{
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan LockWaitLimit = TimeSpan.FromSeconds(10);

    public static async Task InitializeAsync(
        PortalDbContext context,
        string? bootstrapUsername,
        string? bootstrapPasswordHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var transaction = await BeginImmediateTransactionAsync(context, cancellationToken);
        var initializerEntries = new List<EntityEntry>(2);
        Exception? operationFailure = null;

        try
        {
            if (!await context.PortalSettings.AnyAsync(
                    setting => setting.Id == PortalSetting.SingletonId,
                    cancellationToken))
            {
                initializerEntries.Add(context.PortalSettings.Add(new PortalSetting
                {
                    Id = PortalSetting.SingletonId,
                    Key = PortalSetting.SingletonKey,
                    AutoActivateAfterEmailVerification = false,
                    UpdatedUtc = DateTime.UtcNow
                }));
            }

            if (!string.IsNullOrWhiteSpace(bootstrapUsername)
                && !string.IsNullOrWhiteSpace(bootstrapPasswordHash)
                && !await context.AdminUsers.AnyAsync(cancellationToken))
            {
                var username = bootstrapUsername.Trim();
                var now = DateTime.UtcNow;
                initializerEntries.Add(context.AdminUsers.Add(new AdminUser
                {
                    Id = Guid.NewGuid(),
                    Username = username,
                    NormalizedUsername = username.ToUpperInvariant(),
                    PasswordHash = bootstrapPasswordHash,
                    FailedAttempts = 0,
                    CreatedUtc = now,
                    UpdatedUtc = now
                }));
            }

            await context.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            context.ChangeTracker.AcceptAllChanges();
        }
        catch (Exception exception)
        {
            operationFailure = exception;

            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackFailure)
            {
                RecordCleanupFailure(exception, "Rollback", rollbackFailure);
            }

            DetachInitializerEntries(initializerEntries, exception);
        }

        try
        {
            await transaction.DisposeAsync();
        }
        catch (Exception disposeFailure) when (operationFailure is not null)
        {
            RecordCleanupFailure(operationFailure, "Dispose", disposeFailure);
        }
        catch (Exception disposeFailure)
        {
            DetachInitializerEntries(initializerEntries, disposeFailure);
            ExceptionDispatchInfo.Capture(disposeFailure).Throw();
        }

        if (operationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }
    }

    private static void DetachInitializerEntries(
        IEnumerable<EntityEntry> entries,
        Exception? operationFailure = null)
    {
        foreach (var entry in entries)
        {
            try
            {
                entry.State = EntityState.Detached;
            }
            catch (Exception detachFailure) when (operationFailure is not null)
            {
                RecordCleanupFailure(operationFailure, "Detach", detachFailure);
            }
        }
    }

    private static void RecordCleanupFailure(
        Exception operationFailure,
        string phase,
        Exception cleanupFailure)
    {
        var key = $"PortalDbInitializer.{phase}Failure";
        for (var suffix = 2; operationFailure.Data.Contains(key); suffix++)
        {
            key = $"PortalDbInitializer.{phase}Failure{suffix}";
        }

        operationFailure.Data[key] = cleanupFailure;
    }

    private static async Task<IDbContextTransaction> BeginImmediateTransactionAsync(
        PortalDbContext context,
        CancellationToken cancellationToken)
    {
        if (context.Database.GetDbConnection() is not SqliteConnection connection)
        {
            throw new InvalidOperationException("Portal initialization requires SQLite.");
        }

        var originalTimeout = connection.DefaultTimeout;
        var elapsed = Stopwatch.StartNew();
        connection.DefaultTimeout = 1;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    return await context.Database.BeginTransactionAsync(cancellationToken);
                }
                catch (SqliteException exception)
                    when (exception.SqliteErrorCode is 5 or 6 && elapsed.Elapsed < LockWaitLimit)
                {
                    await Task.Delay(LockRetryDelay, cancellationToken);
                }
            }
        }
        finally
        {
            connection.DefaultTimeout = originalTimeout;
        }
    }
}
