using System.Net.Security;
using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Mir3.Web.Options;
using Mir3.Web.Services;

namespace Mir3.Web.Tests;

public sealed class SmtpEmailSenderTests
{
    [Fact]
    public async Task SendAsync_uses_required_transport_settings_credentials_and_configured_sender_once()
    {
        var transport = new RecordingTransport();
        var sender = new SmtpEmailSender(Microsoft.Extensions.Options.Options.Create(CreateOptions()), new RecordingTransportFactory(transport));
        var message = new EmailMessage(
            new EmailAddress("untrusted@example.test", "Untrusted Sender"),
            new EmailAddress("player@example.test"),
            "subject",
            "text",
            "<p>html</p>");

        await sender.SendAsync(message);

        Assert.Equal(20_000, transport.Timeout);
        Assert.Equal(("smtp.example.invalid", 587, SecureSocketOptions.StartTls), transport.ConnectCall);
        Assert.Equal(("smtp-user-secret", "smtp-password-secret"), transport.AuthenticationCall);
        Assert.Equal(1, transport.SendCount);
        Assert.Equal("mir3@example.invalid", transport.SentFromAddress);
        Assert.Equal("Mir3 Zircon", transport.SentFromName);
        Assert.Equal(0, transport.CertificateValidationCallbackSetCount);
        Assert.Null(transport.ServerCertificateValidationCallback);
    }

    [Fact]
    public async Task SendAsync_propagates_caller_cancellation_without_mapping_it_to_an_smtp_failure()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new RecordingTransport
        {
            Connect = cancellationToken =>
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        };
        var sender = new SmtpEmailSender(Microsoft.Extensions.Options.Options.Create(CreateOptions()), new RecordingTransportFactory(transport));
        var message = new EmailMessage(
            new EmailAddress("mir3@example.invalid", "Mir3 Zircon"),
            new EmailAddress("player@example.test"),
            "subject",
            "text",
            "<p>html</p>");

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sender.SendAsync(message, cancellation.Token));

        Assert.IsNotType<SmtpEmailException>(exception);
        Assert.Equal(1, transport.ConnectCount);
        Assert.Null(transport.AuthenticationCall);
        Assert.Equal(0, transport.SendCount);
    }

    [Theory]
    [InlineData(SmtpPhase.Connect)]
    [InlineData(SmtpPhase.Authenticate)]
    [InlineData(SmtpPhase.Send)]
    public async Task SendAsync_propagates_caller_cancellation_from_every_phase(SmtpPhase phase)
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new RecordingTransport();
        transport.SetPhase(phase, cancellationToken =>
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        var sender = CreateSender(transport);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sender.SendAsync(CreateMessage(), cancellation.Token));

        Assert.IsNotType<SmtpEmailException>(exception);
    }

    [Fact]
    public async Task SendAsync_classifies_credential_rejection_as_permanent_authentication_failure()
    {
        var transport = new RecordingTransport
        {
            Authenticate = _ => Task.FromException(new MailKit.Security.AuthenticationException("raw credential detail"))
        };

        var exception = await Assert.ThrowsAsync<SmtpEmailException>(() => CreateSender(transport).SendAsync(CreateMessage()));

        Assert.Equal(SmtpFailureCode.Authentication, exception.FailureCode);
        Assert.False(exception.IsTransient);
        Assert.Equal("smtp-auth", exception.Message);
        Assert.DoesNotContain("credential", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SmtpPhase.Connect, 421, SmtpFailureCode.Connect, true)]
    [InlineData(SmtpPhase.Connect, 554, SmtpFailureCode.Connect, false)]
    [InlineData(SmtpPhase.Authenticate, 454, SmtpFailureCode.Authentication, true)]
    [InlineData(SmtpPhase.Authenticate, 535, SmtpFailureCode.Authentication, false)]
    [InlineData(SmtpPhase.Send, 451, SmtpFailureCode.Send, true)]
    [InlineData(SmtpPhase.Send, 550, SmtpFailureCode.Send, false)]
    public async Task SendAsync_classifies_smtp_status_by_response_class(
        SmtpPhase phase,
        int status,
        SmtpFailureCode expectedCode,
        bool expectedTransient)
    {
        var transport = new RecordingTransport();
        transport.SetPhase(
            phase,
            _ => Task.FromException(new SmtpCommandException(
                SmtpErrorCode.UnexpectedStatusCode,
                (SmtpStatusCode)status,
                "raw server detail")));

        var exception = await Assert.ThrowsAsync<SmtpEmailException>(() => CreateSender(transport).SendAsync(CreateMessage()));

        Assert.Equal(expectedCode, exception.FailureCode);
        Assert.Equal(expectedTransient, exception.IsTransient);
        Assert.DoesNotContain("server detail", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<Exception> TransientTransportFailures => new()
    {
        new TimeoutException("raw timeout detail"),
        new IOException("raw I/O detail"),
        new SocketException((int)SocketError.ConnectionReset),
        new SmtpProtocolException("raw protocol detail"),
        new OperationCanceledException("internal timeout detail")
    };

    [Theory]
    [MemberData(nameof(TransientTransportFailures))]
    public async Task SendAsync_classifies_transport_interruptions_as_transient(Exception failure)
    {
        var transport = new RecordingTransport
        {
            Send = _ => Task.FromException(failure)
        };

        var exception = await Assert.ThrowsAsync<SmtpEmailException>(() => CreateSender(transport).SendAsync(CreateMessage()));

        Assert.Equal(SmtpFailureCode.Send, exception.FailureCode);
        Assert.True(exception.IsTransient);
        Assert.DoesNotContain("detail", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendAsync_classifies_unknown_programming_error_as_non_retryable_safe_failure()
    {
        var transport = new RecordingTransport
        {
            Send = _ => Task.FromException(new InvalidOperationException("raw programming detail"))
        };

        var exception = await Assert.ThrowsAsync<SmtpEmailException>(() => CreateSender(transport).SendAsync(CreateMessage()));

        Assert.Equal(SmtpFailureCode.Send, exception.FailureCode);
        Assert.False(exception.IsTransient);
        Assert.Equal("smtp-send", exception.Message);
        Assert.DoesNotContain("programming detail", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static SmtpEmailSender CreateSender(RecordingTransport transport) =>
        new(Microsoft.Extensions.Options.Options.Create(CreateOptions()), new RecordingTransportFactory(transport));

    private static EmailMessage CreateMessage() => new(
        new EmailAddress("mir3@example.invalid", "Mir3 Zircon"),
        new EmailAddress("player@example.test"),
        "subject",
        "text",
        "<p>html</p>");

    private static SmtpOptions CreateOptions() => new()
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

    private sealed class RecordingTransportFactory(RecordingTransport transport) : IMailKitSmtpClientFactory
    {
        public IMailKitSmtpClient Create() => transport;
    }

    private sealed class RecordingTransport : IMailKitSmtpClient
    {
        private RemoteCertificateValidationCallback? _serverCertificateValidationCallback;

        public int Timeout { get; set; }

        public bool IsConnected { get; private set; }

        public int ConnectCount { get; private set; }

        public (string Host, int Port, SecureSocketOptions Options)? ConnectCall { get; private set; }

        public (string Username, string Password)? AuthenticationCall { get; private set; }

        public int SendCount { get; private set; }

        public string? SentFromAddress { get; private set; }

        public string? SentFromName { get; private set; }

        public int CertificateValidationCallbackSetCount { get; private set; }

        public Func<CancellationToken, Task>? Connect { get; set; }

        public Func<CancellationToken, Task>? Authenticate { get; set; }

        public Func<CancellationToken, Task>? Send { get; set; }

        public void SetPhase(SmtpPhase phase, Func<CancellationToken, Task> action)
        {
            switch (phase)
            {
                case SmtpPhase.Connect:
                    Connect = action;
                    break;
                case SmtpPhase.Authenticate:
                    Authenticate = action;
                    break;
                case SmtpPhase.Send:
                    Send = action;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(phase));
            }
        }

        public RemoteCertificateValidationCallback? ServerCertificateValidationCallback
        {
            get => _serverCertificateValidationCallback;
            set
            {
                CertificateValidationCallbackSetCount++;
                _serverCertificateValidationCallback = value;
            }
        }

        public async Task ConnectAsync(
            string host,
            int port,
            SecureSocketOptions options,
            CancellationToken cancellationToken)
        {
            ConnectCount++;
            ConnectCall = (host, port, options);
            if (Connect is not null)
                await Connect(cancellationToken);
            IsConnected = true;
        }

        public async Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
        {
            AuthenticationCall = (username, password);
            if (Authenticate is not null)
                await Authenticate(cancellationToken);
        }

        public async Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken)
        {
            SendCount++;
            var from = Assert.IsType<MailboxAddress>(Assert.Single(message.From));
            SentFromAddress = from.Address;
            SentFromName = from.Name;
            if (Send is not null)
                await Send(cancellationToken);
            return string.Empty;
        }

        public Task DisconnectAsync(bool quit, CancellationToken cancellationToken)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    public enum SmtpPhase
    {
        Connect,
        Authenticate,
        Send
    }
}
