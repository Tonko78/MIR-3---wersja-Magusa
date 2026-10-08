using System.Net;
using Mir3.Web.Services;
using Mir3.Web.Pages;

namespace Mir3.Web.Tests;

public sealed class StatusPageTests
{
    [Theory]
    [InlineData("Waiting for e-mail verification", "status-badge--pending", "✉")]
    [InlineData("Waiting for approval", "status-badge--pending", "…")]
    [InlineData("Processing", "status-badge--processing", "↻")]
    [InlineData("Active", "status-badge--active", "✓")]
    [InlineData("Unavailable", "status-badge--unavailable", "!")]
    [InlineData("Processing delayed", "status-badge--delayed", "!")]
    public async Task Known_public_statuses_use_their_accessible_presentation(
        string publicStatus,
        string expectedClass,
        string expectedIcon)
    {
        var model = new StatusModel(new StubRegistrationService(
            new RegistrationStatusResult(
                RegistrationStatusOutcome.Found,
                publicStatus,
                $"Registration status: {publicStatus}.",
                "player@example.test")));

        await model.OnGetAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(publicStatus, model.PublicStatus);
        Assert.Equal(expectedClass, model.StatusBadgeClass);
        Assert.Equal(expectedIcon, model.StatusIcon);
        Assert.Equal("player@example.test", model.EmailAddress);
    }

    [Fact]
    public async Task Found_status_displays_the_registration_email()
    {
        var model = new StatusModel(new StubRegistrationService(
            new RegistrationStatusResult(
                RegistrationStatusOutcome.Found,
                "Active",
                "Registration status: Active.",
                "lewy@example.test")));

        await model.OnGetAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("lewy@example.test", model.EmailAddress);
    }

    [Fact]
    public async Task Unknown_public_status_is_replaced_with_generic_unavailable_presentation()
    {
        var model = new StatusModel(new StubRegistrationService(
            new RegistrationStatusResult(
                RegistrationStatusOutcome.Found,
                "QueuePending",
                "Registration status: QueuePending.",
                "player@example.test")));

        await model.OnGetAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("Unavailable", model.PublicStatus);
        Assert.Equal(RegistrationMessages.StatusUnavailable, model.Message);
        Assert.Equal("status-badge--unavailable", model.StatusBadgeClass);
        Assert.Equal("!", model.StatusIcon);
        Assert.DoesNotContain("QueuePending", model.Message, StringComparison.Ordinal);
        Assert.Null(model.EmailAddress);
    }

    private sealed class StubRegistrationService(RegistrationStatusResult result) : IRegistrationService
    {
        public Task<RegistrationStatusResult> GetStatusAsync(Guid registrationReference, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);

        public Task<RegistrationCreateResult> RegisterAsync(string email, string password, IPAddress sourceIp, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RegistrationVerificationResult> VerifyAsync(string? token, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ResendVerificationAsync(Guid registrationReference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
