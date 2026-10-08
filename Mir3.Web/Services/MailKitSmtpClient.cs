using System.Net.Security;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Mir3.Web.Services;

internal interface IMailKitSmtpClientFactory
{
    IMailKitSmtpClient Create();
}

internal interface IMailKitSmtpClient : IDisposable
{
    int Timeout { get; set; }

    bool IsConnected { get; }

    RemoteCertificateValidationCallback? ServerCertificateValidationCallback { get; set; }

    Task ConnectAsync(
        string host,
        int port,
        SecureSocketOptions options,
        CancellationToken cancellationToken);

    Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken);

    Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken);

    Task DisconnectAsync(bool quit, CancellationToken cancellationToken);
}

internal sealed class MailKitSmtpClientFactory : IMailKitSmtpClientFactory
{
    public IMailKitSmtpClient Create() => new MailKitSmtpClient();
}

internal sealed class MailKitSmtpClient : IMailKitSmtpClient
{
    private readonly SmtpClient _client = new();

    public int Timeout
    {
        get => _client.Timeout;
        set => _client.Timeout = value;
    }

    public bool IsConnected => _client.IsConnected;

    public RemoteCertificateValidationCallback? ServerCertificateValidationCallback
    {
        get => _client.ServerCertificateValidationCallback;
        set => _client.ServerCertificateValidationCallback = value;
    }

    public Task ConnectAsync(
        string host,
        int port,
        SecureSocketOptions options,
        CancellationToken cancellationToken) =>
        _client.ConnectAsync(host, port, options, cancellationToken);

    public Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken) =>
        _client.AuthenticateAsync(username, password, cancellationToken);

    public Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken) =>
        _client.SendAsync(message, cancellationToken);

    public Task DisconnectAsync(bool quit, CancellationToken cancellationToken) =>
        _client.DisconnectAsync(quit, cancellationToken);

    public void Dispose() => _client.Dispose();
}
