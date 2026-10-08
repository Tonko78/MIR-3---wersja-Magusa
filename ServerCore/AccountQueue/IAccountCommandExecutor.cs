using AccountPortal.Contracts;
using System;

namespace Server.AccountQueue;

public interface IAccountDirectory
{
    bool Exists(string email);
    void Create(string email, byte[] passwordHash);
    bool SetActivated(string email, bool activated);
    bool SetPassword(string email, byte[] passwordHash);
    void Disconnect(string email);
    void Commit();
}

public interface IAccountCommandExecutor
{
    AccountCommandResult Execute(AccountCommand command, DateTimeOffset now);
}
