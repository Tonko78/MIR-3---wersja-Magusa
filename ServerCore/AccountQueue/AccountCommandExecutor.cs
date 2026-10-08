using AccountPortal.Contracts;
using System;
using System.IO;

namespace Server.AccountQueue;

public sealed class AccountCommandExecutor(IAccountDirectory directory) : IAccountCommandExecutor
{
    public AccountCommandResult Execute(AccountCommand command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            command.Validate();
        }
        catch (InvalidDataException)
        {
            return InvalidResult(command, now);
        }

        var email = command.Email.Trim().ToLowerInvariant();

        return command.Type switch
        {
            AccountCommandType.CreateAccount => Create(command, email, now),
            AccountCommandType.ActivateAccount => SetActivated(command, email, true, now),
            AccountCommandType.DeactivateAccount => SetActivated(command, email, false, now),
            AccountCommandType.ResetPassword => ResetPassword(command, email, now),
            _ => Result(command, AccountCommandStatus.Invalid, "invalid-command", email, now)
        };
    }

    private AccountCommandResult Create(AccountCommand command, string email, DateTimeOffset now)
    {
        if (directory.Exists(email))
        {
            return Result(command, AccountCommandStatus.Conflict, "already-exists", email, now);
        }

        var passwordHash = Convert.FromBase64String(command.PasswordHashBase64!);
        directory.Create(email, passwordHash);
        directory.Commit();

        return Result(command, AccountCommandStatus.Success, "created", email, now);
    }

    private AccountCommandResult ResetPassword(AccountCommand command, string email, DateTimeOffset now)
    {
        var passwordHash = Convert.FromBase64String(command.PasswordHashBase64!);
        if (!directory.SetPassword(email, passwordHash))
        {
            return Result(command, AccountCommandStatus.NotFound, "account-not-found", email, now);
        }

        directory.Commit();
        return Result(command, AccountCommandStatus.Success, "password-reset", email, now);
    }

    private AccountCommandResult SetActivated(
        AccountCommand command,
        string email,
        bool activated,
        DateTimeOffset now)
    {
        if (!directory.SetActivated(email, activated))
        {
            return Result(command, AccountCommandStatus.NotFound, "account-not-found", email, now);
        }

        directory.Commit();

        if (!activated)
        {
            directory.Disconnect(email);
        }

        return Result(command, AccountCommandStatus.Success, activated ? "activated" : "deactivated", email, now);
    }

    private static AccountCommandResult InvalidResult(AccountCommand command, DateTimeOffset now)
    {
        return Result(
            command,
            AccountCommandStatus.Invalid,
            "invalid-command",
            command.Email ?? string.Empty,
            now);
    }

    private static AccountCommandResult Result(
        AccountCommand command,
        AccountCommandStatus status,
        string code,
        string email,
        DateTimeOffset now)
    {
        return new AccountCommandResult(command.Id, status, code, email, now);
    }
}
