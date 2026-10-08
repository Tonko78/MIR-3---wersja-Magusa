using System.Text.Json;
using AccountPortal.Contracts;
using Mir3.Web.Domain;
using Mir3.Web.Services;

namespace Mir3.Web.Tests;

public sealed class RegistrationStateMachineTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
    private readonly RegistrationStateMachine _machine = new();

    [Fact]
    public void Verify_without_auto_activation_moves_pending_email_to_awaiting_admin()
    {
        var registration = CreateRegistration(RegistrationStatus.PendingEmail);

        var command = _machine.Verify(registration, autoActivate: false, queueRequestId: null, Now);

        Assert.Null(command);
        Assert.Equal(RegistrationStatus.AwaitingAdmin, registration.Status);
        Assert.Equal(Now, registration.EmailVerifiedUtc);
        Assert.Equal(Now, registration.VerificationUsedUtc);
        Assert.Null(registration.QueueRequestId);
        Assert.Null(registration.QueueCommandType);
        Assert.Null(registration.QueueLastErrorCode);
        Assert.Equal(Now, registration.UpdatedUtc);
    }

    [Fact]
    public void Verify_with_auto_activation_queues_create_account()
    {
        var registration = CreateRegistration(RegistrationStatus.PendingEmail);
        var requestId = Guid.NewGuid();

        var command = _machine.Verify(registration, autoActivate: true, requestId, Now);

        Assert.Equal(AccountCommandType.CreateAccount, command);
        AssertQueuePending(registration, requestId, AccountCommandType.CreateAccount);
        Assert.Equal(Now, registration.EmailVerifiedUtc);
        Assert.Equal(Now, registration.VerificationUsedUtc);
    }

    [Fact]
    public void Admin_activate_awaiting_registration_queues_create_account()
    {
        var registration = CreateRegistration(RegistrationStatus.AwaitingAdmin);
        registration.EmailVerifiedUtc = Now.AddMinutes(-1);
        var requestId = Guid.NewGuid();

        var command = _machine.AdminActivate(registration, requestId, Now);

        Assert.Equal(AccountCommandType.CreateAccount, command);
        AssertQueuePending(registration, requestId, AccountCommandType.CreateAccount);
    }

    [Fact]
    public void Admin_deactivate_active_registration_queues_deactivation()
    {
        var registration = CreateRegistration(RegistrationStatus.Active);
        var requestId = Guid.NewGuid();

        var command = _machine.AdminDeactivate(registration, requestId, Now);

        Assert.Equal(AccountCommandType.DeactivateAccount, command);
        AssertQueuePending(registration, requestId, AccountCommandType.DeactivateAccount);
    }

    [Fact]
    public void Admin_reset_password_active_registration_queues_reset_without_replacing_current_hash()
    {
        var registration = CreateRegistration(RegistrationStatus.Active);
        var currentHash = registration.PasswordHash.ToArray();
        var replacementHash = Enumerable.Range(36, 36).Select(value => (byte)value).ToArray();
        var requestId = Guid.NewGuid();

        var command = _machine.AdminResetPassword(registration, replacementHash, requestId, Now);

        Assert.Equal(AccountCommandType.ResetPassword, command);
        AssertQueuePending(registration, requestId, AccountCommandType.ResetPassword);
        Assert.Equal(currentHash, registration.PasswordHash);
        Assert.Equal(replacementHash, registration.PendingPasswordHash);
    }

    [Fact]
    public void Admin_activate_disabled_registration_queues_activation()
    {
        var registration = CreateRegistration(RegistrationStatus.Disabled);
        registration.EmailVerifiedUtc = Now.AddMinutes(-1);
        var requestId = Guid.NewGuid();

        var command = _machine.AdminActivate(registration, requestId, Now);

        Assert.Equal(AccountCommandType.ActivateAccount, command);
        AssertQueuePending(registration, requestId, AccountCommandType.ActivateAccount);
    }

    [Theory]
    [InlineData(RegistrationStatus.AwaitingAdmin)]
    [InlineData(RegistrationStatus.Disabled)]
    public void Admin_activate_requires_email_verification_for_eligible_statuses(RegistrationStatus status)
    {
        var registration = CreateRegistration(status);
        var before = JsonSerializer.Serialize(registration);

        Assert.Throws<RegistrationTransitionException>(() =>
            _machine.AdminActivate(registration, Guid.NewGuid(), Now));

        Assert.Equal(before, JsonSerializer.Serialize(registration));
    }

    [Theory]
    [InlineData(AccountCommandType.CreateAccount, "created")]
    [InlineData(AccountCommandType.ActivateAccount, "activated")]
    public void Successful_create_or_activation_moves_queue_pending_to_active(
        AccountCommandType commandType,
        string code)
    {
        var registration = QueuedRegistration(commandType);

        _machine.ApplyResult(registration, Result(registration, AccountCommandStatus.Success, code), Now);

        Assert.Equal(RegistrationStatus.Active, registration.Status);
        Assert.Equal(Now, registration.GameActivatedUtc);
        Assert.Null(registration.AdminDisabledUtc);
        Assert.Null(registration.QueueLastErrorCode);
        Assert.Equal(Now, registration.UpdatedUtc);
    }

    [Fact]
    public void Successful_deactivation_moves_queue_pending_to_disabled()
    {
        var registration = QueuedRegistration(AccountCommandType.DeactivateAccount);

        _machine.ApplyResult(registration, Result(registration, AccountCommandStatus.Success, "deactivated"), Now);

        Assert.Equal(RegistrationStatus.Disabled, registration.Status);
        Assert.Equal(Now, registration.AdminDisabledUtc);
        Assert.Null(registration.QueueLastErrorCode);
        Assert.Equal(Now, registration.UpdatedUtc);
    }

    [Fact]
    public void Successful_password_reset_commits_pending_hash_and_keeps_account_active()
    {
        var registration = QueuedRegistration(AccountCommandType.ResetPassword);
        var replacementHash = Enumerable.Range(36, 36).Select(value => (byte)value).ToArray();
        registration.PendingPasswordHash = replacementHash;

        _machine.ApplyResult(registration, Result(registration, AccountCommandStatus.Success, "password-reset"), Now);

        Assert.Equal(RegistrationStatus.Active, registration.Status);
        Assert.Equal(replacementHash, registration.PasswordHash);
        Assert.Null(registration.PendingPasswordHash);
        Assert.Null(registration.QueueLastErrorCode);
    }

    [Fact]
    public void Failed_password_reset_restores_active_state_and_discards_pending_hash()
    {
        var registration = QueuedRegistration(AccountCommandType.ResetPassword);
        var currentHash = registration.PasswordHash.ToArray();
        registration.PendingPasswordHash = new byte[36];

        _machine.ApplyResult(registration, Result(registration, AccountCommandStatus.Failed, "execution-failed"), Now);

        Assert.Equal(RegistrationStatus.Active, registration.Status);
        Assert.Equal(currentHash, registration.PasswordHash);
        Assert.Null(registration.PendingPasswordHash);
        Assert.Equal("execution-failed", registration.QueueLastErrorCode);
    }

    [Fact]
    public void Already_existing_create_is_treated_as_active()
    {
        var registration = QueuedRegistration(AccountCommandType.CreateAccount);

        _machine.ApplyResult(registration, Result(registration, AccountCommandStatus.Conflict, "already-exists"), Now);

        Assert.Equal(RegistrationStatus.Active, registration.Status);
        Assert.Equal(Now, registration.GameActivatedUtc);
        Assert.Null(registration.QueueLastErrorCode);
    }

    [Theory]
    [InlineData(AccountCommandStatus.Failed, "execution-failed")]
    [InlineData(AccountCommandStatus.Rejected, "invalid-signature")]
    public void Failed_or_rejected_result_moves_queue_pending_to_failed_with_safe_code(
        AccountCommandStatus status,
        string code)
    {
        var registration = QueuedRegistration(AccountCommandType.CreateAccount);

        _machine.ApplyResult(registration, Result(registration, status, code), Now);

        Assert.Equal(RegistrationStatus.Failed, registration.Status);
        Assert.Equal(code, registration.QueueLastErrorCode);
        Assert.Null(registration.QueueRequestId);
        Assert.Null(registration.QueueCommandType);
        Assert.Null(registration.QueueLastCheckedUtc);
        Assert.Equal(Now, registration.UpdatedUtc);
    }

    [Theory]
    [InlineData(AccountCommandType.ActivateAccount)]
    [InlineData(AccountCommandType.DeactivateAccount)]
    public void Missing_account_terminal_result_fails_activation_or_deactivation_with_only_safe_code(
        AccountCommandType commandType)
    {
        var registration = QueuedRegistration(commandType);
        registration.QueueLastCheckedUtc = Now.AddMinutes(-1);

        _machine.ApplyResult(
            registration,
            Result(registration, AccountCommandStatus.NotFound, "account-not-found"),
            Now);

        Assert.Equal(RegistrationStatus.Failed, registration.Status);
        Assert.Equal("account-not-found", registration.QueueLastErrorCode);
        Assert.Null(registration.QueueRequestId);
        Assert.Null(registration.QueueCommandType);
        Assert.Null(registration.QueueLastCheckedUtc);
        Assert.Equal(Now, registration.UpdatedUtc);
    }

    [Theory]
    [InlineData(AccountCommandType.CreateAccount, "account-not-found")]
    [InlineData(AccountCommandType.ActivateAccount, "other-not-found")]
    [InlineData(AccountCommandType.DeactivateAccount, "execution-failed")]
    public void Not_found_is_rejected_unless_command_and_code_are_exactly_allow_listed(
        AccountCommandType commandType,
        string code)
    {
        var registration = QueuedRegistration(commandType);
        var before = JsonSerializer.Serialize(registration);

        Assert.Throws<RegistrationTransitionException>(() =>
            _machine.ApplyResult(registration, Result(registration, AccountCommandStatus.NotFound, code), Now));

        Assert.Equal(before, JsonSerializer.Serialize(registration));
    }

    [Fact]
    public void Unsafe_result_error_code_is_replaced_not_persisted()
    {
        var registration = QueuedRegistration(AccountCommandType.CreateAccount);

        _machine.ApplyResult(
            registration,
            Result(registration, AccountCommandStatus.Failed, "secret path /opt/zircon and exception"),
            Now);

        Assert.Equal("queue-failed", registration.QueueLastErrorCode);
    }

    public static TheoryData<Action<RegistrationStateMachine, Registration>, RegistrationStatus> InvalidTransitions => new()
    {
        { (machine, registration) => machine.Verify(registration, false, null, Now), RegistrationStatus.Active },
        { (machine, registration) => machine.AdminActivate(registration, Guid.NewGuid(), Now), RegistrationStatus.PendingEmail },
        { (machine, registration) => machine.AdminDeactivate(registration, Guid.NewGuid(), Now), RegistrationStatus.Disabled },
        { (machine, registration) => machine.ApplyResult(registration, Result(registration, AccountCommandStatus.Success, "created"), Now), RegistrationStatus.Active }
    };

    [Theory]
    [MemberData(nameof(InvalidTransitions))]
    public void Invalid_transition_throws_and_preserves_complete_entity_state(
        Action<RegistrationStateMachine, Registration> transition,
        RegistrationStatus status)
    {
        var registration = CreateRegistration(status);
        registration.QueueRequestId = Guid.NewGuid();
        registration.QueueCommandType = AccountCommandType.ActivateAccount;
        registration.QueueLastErrorCode = "existing-error";
        var before = JsonSerializer.Serialize(registration);

        var exception = Assert.Throws<RegistrationTransitionException>(() => transition(_machine, registration));

        Assert.Contains(status.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(registration));
    }

    [Fact]
    public void Mismatched_result_identity_throws_and_preserves_complete_entity_state()
    {
        var registration = QueuedRegistration(AccountCommandType.CreateAccount);
        var before = JsonSerializer.Serialize(registration);
        var result = new AccountCommandResult(
            Guid.NewGuid(),
            AccountCommandStatus.Success,
            "created",
            registration.NormalizedEmail,
            new DateTimeOffset(Now));

        Assert.Throws<RegistrationTransitionException>(() => _machine.ApplyResult(registration, result, Now));
        Assert.Equal(before, JsonSerializer.Serialize(registration));
    }

    [Fact]
    public void Null_result_email_throws_focused_exception_without_mutation()
    {
        var registration = QueuedRegistration(AccountCommandType.CreateAccount);
        var before = JsonSerializer.Serialize(registration);
        var result = new AccountCommandResult(
            registration.QueueRequestId!.Value,
            AccountCommandStatus.Success,
            "created",
            null!,
            new DateTimeOffset(Now));

        Assert.Throws<RegistrationTransitionException>(() => _machine.ApplyResult(registration, result, Now));
        Assert.Equal(before, JsonSerializer.Serialize(registration));
    }

    [Fact]
    public void Result_code_not_matching_pending_command_throws_without_mutation()
    {
        var registration = QueuedRegistration(AccountCommandType.DeactivateAccount);
        var before = JsonSerializer.Serialize(registration);

        Assert.Throws<RegistrationTransitionException>(() =>
            _machine.ApplyResult(registration, Result(registration, AccountCommandStatus.Success, "activated"), Now));

        Assert.Equal(before, JsonSerializer.Serialize(registration));
    }

    [Fact]
    public void Queueing_transition_requires_nonempty_correlation_id_without_mutation()
    {
        var registration = CreateRegistration(RegistrationStatus.PendingEmail);
        var before = JsonSerializer.Serialize(registration);

        Assert.Throws<RegistrationTransitionException>(() =>
            _machine.Verify(registration, autoActivate: true, Guid.Empty, Now));

        Assert.Equal(before, JsonSerializer.Serialize(registration));
    }

    private static Registration QueuedRegistration(AccountCommandType commandType)
    {
        var registration = CreateRegistration(RegistrationStatus.QueuePending);
        registration.QueueRequestId = Guid.NewGuid();
        registration.QueueCommandType = commandType;
        return registration;
    }

    private static AccountCommandResult Result(
        Registration registration,
        AccountCommandStatus status,
        string code) =>
        new(
            registration.QueueRequestId!.Value,
            status,
            code,
            registration.NormalizedEmail,
            new DateTimeOffset(Now));

    private static Registration CreateRegistration(RegistrationStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Email = "Player@Example.test",
        NormalizedEmail = "player@example.test",
        PasswordHash = Enumerable.Range(0, 36).Select(value => (byte)value).ToArray(),
        Status = status,
        VerificationTokenHash = new byte[32],
        VerificationExpiresUtc = Now.AddHours(1),
        CreatedUtc = Now.AddHours(-1),
        UpdatedUtc = Now.AddMinutes(-1),
        SourceIpHash = new byte[32]
    };

    private static void AssertQueuePending(
        Registration registration,
        Guid requestId,
        AccountCommandType commandType)
    {
        Assert.Equal(RegistrationStatus.QueuePending, registration.Status);
        Assert.Equal(requestId, registration.QueueRequestId);
        Assert.Equal(commandType, registration.QueueCommandType);
        Assert.Null(registration.QueueLastErrorCode);
        Assert.Equal(Now, registration.UpdatedUtc);
    }
}
