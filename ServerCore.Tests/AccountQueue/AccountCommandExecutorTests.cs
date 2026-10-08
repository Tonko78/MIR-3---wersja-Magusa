using AccountPortal.Contracts;
using Server.AccountQueue;

namespace ServerCore.Tests.AccountQueue;

public sealed class AccountCommandExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Execute_WithNullCommand_ThrowsArgumentNullException()
    {
        var directory = new FakeAccountDirectory();

        var exception = Assert.Throws<ArgumentNullException>(
            () => new AccountCommandExecutor(directory).Execute(null!, Now));

        Assert.Equal("command", exception.ParamName);
        Assert.Empty(directory.Operations);
    }

    [Fact]
    public void CreateAccount_CreatesNormalizedAccountAndCommitsOnce()
    {
        var directory = new FakeAccountDirectory();
        var hash = Enumerable.Range(0, 36).Select(value => (byte)value).ToArray();
        var command = Command(AccountCommandType.CreateAccount, "  PLAYER@Example.COM  ", hash);

        var result = new AccountCommandExecutor(directory).Execute(command, Now);

        Assert.Equal(AccountCommandStatus.Success, result.Status);
        Assert.Equal("created", result.Code);
        Assert.Equal("player@example.com", result.Email);
        Assert.Equal(Now, result.CompletedAtUtc);
        Assert.Equal("player@example.com", directory.CreatedEmail);
        Assert.Equal(hash, directory.CreatedPasswordHash);
        Assert.Equal(1, directory.CommitCount);
    }

    [Fact]
    public void CreateAccount_WhenAccountExists_ReturnsConflictWithoutMutation()
    {
        var directory = new FakeAccountDirectory("player@example.com");

        var result = new AccountCommandExecutor(directory).Execute(
            Command(AccountCommandType.CreateAccount, "PLAYER@example.com", new byte[36]),
            Now);

        Assert.Equal(AccountCommandStatus.Conflict, result.Status);
        Assert.Equal("already-exists", result.Code);
        Assert.Null(directory.CreatedEmail);
        Assert.Equal(0, directory.CommitCount);
    }

    [Theory]
    [InlineData(AccountCommandType.ActivateAccount)]
    [InlineData(AccountCommandType.DeactivateAccount)]
    public void ChangeActivation_WhenAccountDoesNotExist_ReturnsNotFoundWithoutMutation(AccountCommandType type)
    {
        var directory = new FakeAccountDirectory();

        var result = new AccountCommandExecutor(directory).Execute(Command(type), Now);

        Assert.Equal(AccountCommandStatus.NotFound, result.Status);
        Assert.Equal("account-not-found", result.Code);
        Assert.Equal(0, directory.CommitCount);
        Assert.Empty(directory.ActivationChanges);
        Assert.Empty(directory.DisconnectedEmails);
    }

    [Fact]
    public void ActivateAccount_SetsActivatedAndCommits()
    {
        var directory = new FakeAccountDirectory("player@example.com");

        var result = new AccountCommandExecutor(directory).Execute(
            Command(AccountCommandType.ActivateAccount),
            Now);

        Assert.Equal(AccountCommandStatus.Success, result.Status);
        Assert.Equal("activated", result.Code);
        Assert.Equal(("player@example.com", true), Assert.Single(directory.ActivationChanges));
        Assert.Equal(1, directory.CommitCount);
        Assert.Empty(directory.DisconnectedEmails);
    }

    [Fact]
    public void DeactivateAccount_SetsInactiveCommitsThenDisconnects()
    {
        var directory = new FakeAccountDirectory("player@example.com");

        var result = new AccountCommandExecutor(directory).Execute(
            Command(AccountCommandType.DeactivateAccount),
            Now);

        Assert.Equal(AccountCommandStatus.Success, result.Status);
        Assert.Equal("deactivated", result.Code);
        Assert.Equal(("player@example.com", false), Assert.Single(directory.ActivationChanges));
        Assert.Equal("player@example.com", Assert.Single(directory.DisconnectedEmails));
        Assert.Equal(new[] { "set:False", "commit", "disconnect" }, directory.Operations);
        Assert.Equal(1, directory.CommitCount);
    }

    [Fact]
    public void ResetPassword_SetsHashResetsWrongPasswordCountAndCommits()
    {
        var directory = new FakeAccountDirectory("player@example.com");
        var hash = Enumerable.Range(0, 36).Select(value => (byte)value).ToArray();

        var result = new AccountCommandExecutor(directory).Execute(
            Command(AccountCommandType.ResetPassword, passwordHash: hash),
            Now);

        Assert.Equal(AccountCommandStatus.Success, result.Status);
        Assert.Equal("password-reset", result.Code);
        Assert.Equal(hash, directory.ResetPasswordHash);
        Assert.Equal(1, directory.CommitCount);
        Assert.Equal(new[] { "password", "commit" }, directory.Operations);
    }

    [Fact]
    public void ResetPassword_WhenAccountDoesNotExistReturnsNotFoundWithoutCommit()
    {
        var directory = new FakeAccountDirectory();

        var result = new AccountCommandExecutor(directory).Execute(
            Command(AccountCommandType.ResetPassword, passwordHash: new byte[36]),
            Now);

        Assert.Equal(AccountCommandStatus.NotFound, result.Status);
        Assert.Equal("account-not-found", result.Code);
        Assert.Null(directory.ResetPasswordHash);
        Assert.Equal(0, directory.CommitCount);
    }

    [Fact]
    public void CreateAccount_WithThirtyFiveByteHash_ReturnsInvalidWithoutMutation()
    {
        var directory = new FakeAccountDirectory();

        var result = new AccountCommandExecutor(directory).Execute(
            Command(AccountCommandType.CreateAccount, passwordHash: new byte[35]),
            Now);

        Assert.Equal(AccountCommandStatus.Invalid, result.Status);
        Assert.Equal("invalid-command", result.Code);
        Assert.Null(directory.CreatedEmail);
        Assert.Equal(0, directory.CommitCount);
        Assert.Empty(directory.Operations);
    }

    [Fact]
    public void Execute_WithNullEmail_ReturnsInvalidWithoutDirectoryCalls()
    {
        var directory = new FakeAccountDirectory();
        var command = Command(AccountCommandType.CreateAccount, passwordHash: new byte[36]);
        command.Email = null!;

        var result = new AccountCommandExecutor(directory).Execute(command, Now);

        Assert.Equal(AccountCommandStatus.Invalid, result.Status);
        Assert.Equal("invalid-command", result.Code);
        Assert.Equal(string.Empty, result.Email);
        Assert.Empty(directory.Operations);
    }

    [Fact]
    public void UnsupportedCommandType_ReturnsInvalidWithoutMutation()
    {
        var directory = new FakeAccountDirectory();

        var result = new AccountCommandExecutor(directory).Execute(Command((AccountCommandType)999), Now);

        Assert.Equal(AccountCommandStatus.Invalid, result.Status);
        Assert.Equal("invalid-command", result.Code);
        Assert.Empty(directory.Operations);
    }

    [Theory]
    [InlineData(AccountCommandType.CreateAccount, FailurePoint.Exists)]
    [InlineData(AccountCommandType.CreateAccount, FailurePoint.Create)]
    [InlineData(AccountCommandType.ActivateAccount, FailurePoint.SetActivated)]
    [InlineData(AccountCommandType.ActivateAccount, FailurePoint.Commit)]
    [InlineData(AccountCommandType.DeactivateAccount, FailurePoint.Disconnect)]
    public void InvalidDataDirectoryFailure_Propagates(
        AccountCommandType commandType,
        FailurePoint failurePoint)
    {
        var expected = new InvalidDataException(failurePoint.ToString());
        var existingEmails = commandType == AccountCommandType.CreateAccount
            ? Array.Empty<string>()
            : ["player@example.com"];
        var directory = new FakeAccountDirectory(existingEmails)
        {
            FailurePoint = failurePoint,
            ExceptionToThrow = expected
        };
        var command = Command(
            commandType,
            passwordHash: commandType == AccountCommandType.CreateAccount ? new byte[36] : null);

        var actual = Assert.Throws<InvalidDataException>(
            () => new AccountCommandExecutor(directory).Execute(command, Now));

        Assert.Same(expected, actual);
    }

    [Theory]
    [InlineData(AccountCommandType.CreateAccount, FailurePoint.Exists)]
    [InlineData(AccountCommandType.CreateAccount, FailurePoint.Create)]
    [InlineData(AccountCommandType.ActivateAccount, FailurePoint.SetActivated)]
    [InlineData(AccountCommandType.ActivateAccount, FailurePoint.Commit)]
    [InlineData(AccountCommandType.DeactivateAccount, FailurePoint.Disconnect)]
    public void UnexpectedDirectoryFailure_Propagates(
        AccountCommandType commandType,
        FailurePoint failurePoint)
    {
        var expected = new InvalidOperationException(failurePoint.ToString());
        var existingEmails = commandType == AccountCommandType.CreateAccount
            ? Array.Empty<string>()
            : ["player@example.com"];
        var directory = new FakeAccountDirectory(existingEmails)
        {
            FailurePoint = failurePoint,
            ExceptionToThrow = expected
        };
        var command = Command(
            commandType,
            passwordHash: commandType == AccountCommandType.CreateAccount ? new byte[36] : null);

        var actual = Assert.Throws<InvalidOperationException>(
            () => new AccountCommandExecutor(directory).Execute(command, Now));

        Assert.Same(expected, actual);
    }

    private static AccountCommand Command(
        AccountCommandType type,
        string email = "player@example.com",
        byte[]? passwordHash = null)
    {
        return new AccountCommand
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Type = type,
            Email = email,
            PasswordHashBase64 = passwordHash is null ? null : Convert.ToBase64String(passwordHash),
            IssuedAtUnixSeconds = Now.ToUnixTimeSeconds(),
            Nonce = "test-nonce"
        };
    }

    private sealed class FakeAccountDirectory(params string[] existingEmails) : IAccountDirectory
    {
        private readonly HashSet<string> _emails = new(existingEmails, StringComparer.OrdinalIgnoreCase);

        public FailurePoint FailurePoint { get; init; }
        public Exception? ExceptionToThrow { get; init; }
        public string? CreatedEmail { get; private set; }
        public byte[]? CreatedPasswordHash { get; private set; }
        public byte[]? ResetPasswordHash { get; private set; }
        public List<(string Email, bool Activated)> ActivationChanges { get; } = [];
        public List<string> DisconnectedEmails { get; } = [];
        public List<string> Operations { get; } = [];
        public int CommitCount { get; private set; }

        public bool Exists(string email)
        {
            ThrowIfConfigured(FailurePoint.Exists);
            return _emails.Contains(email);
        }

        public void Create(string email, byte[] passwordHash)
        {
            ThrowIfConfigured(FailurePoint.Create);
            CreatedEmail = email;
            CreatedPasswordHash = passwordHash;
            _emails.Add(email);
            Operations.Add("create");
        }

        public bool SetActivated(string email, bool activated)
        {
            ThrowIfConfigured(FailurePoint.SetActivated);
            if (!_emails.Contains(email)) return false;

            ActivationChanges.Add((email, activated));
            Operations.Add($"set:{activated}");
            return true;
        }

        public bool SetPassword(string email, byte[] passwordHash)
        {
            ThrowIfConfigured(FailurePoint.SetPassword);
            if (!_emails.Contains(email)) return false;

            ResetPasswordHash = passwordHash;
            Operations.Add("password");
            return true;
        }

        public void Disconnect(string email)
        {
            ThrowIfConfigured(FailurePoint.Disconnect);
            DisconnectedEmails.Add(email);
            Operations.Add("disconnect");
        }

        public void Commit()
        {
            ThrowIfConfigured(FailurePoint.Commit);
            CommitCount++;
            Operations.Add("commit");
        }

        private void ThrowIfConfigured(FailurePoint failurePoint)
        {
            if (FailurePoint == failurePoint && ExceptionToThrow is not null) throw ExceptionToThrow;
        }
    }

    public enum FailurePoint
    {
        None,
        Exists,
        Create,
        SetActivated,
        SetPassword,
        Commit,
        Disconnect
    }
}
