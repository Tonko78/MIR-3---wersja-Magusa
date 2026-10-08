using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Mir3.Web.Options;

namespace Mir3.Web.Services;

public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly IMailKitSmtpClientFactory _clientFactory;

    public SmtpEmailSender(IOptions<SmtpOptions> options)
        : this(options, new MailKitSmtpClientFactory())
    {
    }

    internal SmtpEmailSender(
        IOptions<SmtpOptions> options,
        IMailKitSmtpClientFactory clientFactory)
    {
        _options = Validate(options);
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        using var mimeMessage = CreateMessage(message, _options);
        using var client = _clientFactory.Create();
        client.Timeout = checked(_options.TimeoutSeconds * 1000);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        await RunPhaseAsync(
            SmtpFailureCode.Connect,
            () => client.ConnectAsync(
                _options.Host,
                _options.Port,
                SecureSocketOptions.StartTls,
                timeout.Token),
            cancellationToken);
        try
        {
            await RunPhaseAsync(
                SmtpFailureCode.Authentication,
                () => client.AuthenticateAsync(_options.Username!, _options.Password!, timeout.Token),
                cancellationToken);
            await RunPhaseAsync(
                SmtpFailureCode.Send,
                () => client.SendAsync(mimeMessage, timeout.Token),
                cancellationToken);
        }
        finally
        {
            if (client.IsConnected)
            {
                try
                {
                    await client.DisconnectAsync(quit: false, CancellationToken.None);
                }
                catch
                {
                    // Disposal is the final cleanup path; never replace the classified outcome.
                }
            }
        }
    }

    private static MimeMessage CreateMessage(EmailMessage message, SmtpOptions options)
    {
        var result = new MimeMessage();
        result.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        result.To.Add(new MailboxAddress(message.To.Name ?? string.Empty, message.To.Address));
        result.Subject = message.Subject;
        result.Body = new MultipartAlternative
        {
            new TextPart("plain") { Text = message.TextBody },
            new TextPart("html") { Text = message.HtmlBody }
        };
        return result;
    }

    private static async Task RunPhaseAsync(
        SmtpFailureCode failureCode,
        Func<Task> action,
        CancellationToken callerCancellation)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Classify(failureCode, exception);
        }
    }

    private static SmtpEmailException Classify(SmtpFailureCode phase, Exception exception)
    {
        if (exception is MailKit.Security.AuthenticationException)
            return new SmtpEmailException(SmtpFailureCode.Authentication, transient: false);

        if (exception is SmtpCommandException command)
        {
            var status = (int)command.StatusCode;
            return new SmtpEmailException(phase, transient: status is >= 400 and < 500);
        }

        var transient = exception is TimeoutException or IOException or SocketException or
            SmtpProtocolException or OperationCanceledException;
        return new SmtpEmailException(phase, transient);
    }

    private static SmtpOptions Validate(IOptions<SmtpOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        var validation = new SmtpOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, value);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                Microsoft.Extensions.Options.Options.DefaultName,
                typeof(SmtpOptions),
                validation.Failures);
        }

        return value;
    }
}
