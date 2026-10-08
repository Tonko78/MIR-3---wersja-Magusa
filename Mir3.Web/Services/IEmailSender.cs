namespace Mir3.Web.Services;

public sealed record EmailAddress(string Address, string? Name = null);

public sealed record EmailMessage(
    EmailAddress From,
    EmailAddress To,
    string Subject,
    string TextBody,
    string HtmlBody);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

public enum SmtpFailureCode
{
    Connect,
    Authentication,
    Send
}

public sealed class SmtpEmailException : Exception
{
    public SmtpEmailException(SmtpFailureCode failureCode, bool transient)
        : base(ToSafeCode(failureCode))
    {
        FailureCode = failureCode;
        IsTransient = transient;
    }

    public SmtpFailureCode FailureCode { get; }

    public bool IsTransient { get; }

    public string SafeCode => ToSafeCode(FailureCode);

    public static string ToSafeCode(SmtpFailureCode failureCode) => failureCode switch
    {
        SmtpFailureCode.Connect => "smtp-connect",
        SmtpFailureCode.Authentication => "smtp-auth",
        SmtpFailureCode.Send => "smtp-send",
        _ => throw new ArgumentOutOfRangeException(nameof(failureCode))
    };
}
