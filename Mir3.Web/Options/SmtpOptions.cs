using Microsoft.Extensions.Options;
using MimeKit;

namespace Mir3.Web.Options;

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";
    public const string UsernameEnvironmentVariable = "MIR3_SMTP_USERNAME";
    public const string PasswordEnvironmentVariable = "MIR3_SMTP_PASSWORD";

    public string? PublicBaseUrl { get; set; }

    public string Host { get; set; } = "smtp.example.invalid";

    public int Port { get; set; } = 587;

    public bool UseStartTls { get; set; } = true;

    public string FromAddress { get; set; } = "mir3@example.invalid";

    public string FromName { get; set; } = "Mir3 Zircon";

    public int TimeoutSeconds { get; set; } = 20;

    public string? Username { get; set; }

    public string? Password { get; set; }
}

public sealed class SmtpOptionsSetup(
    IConfiguration configuration,
    Func<string, string?>? readEnvironmentVariable = null) : IConfigureOptions<SmtpOptions>
{
    private readonly Func<string, string?> _readEnvironmentVariable =
        readEnvironmentVariable ?? Environment.GetEnvironmentVariable;

    public void Configure(SmtpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        configuration.GetSection(SmtpOptions.SectionName).Bind(options);
        options.PublicBaseUrl = configuration["MIR3_PUBLIC_BASE_URL"] ?? _readEnvironmentVariable("MIR3_PUBLIC_BASE_URL");
        options.Username = _readEnvironmentVariable(SmtpOptions.UsernameEnvironmentVariable);
        options.Password = _readEnvironmentVariable(SmtpOptions.PasswordEnvironmentVariable);
    }
}

public sealed class SmtpOptionsValidator : IValidateOptions<SmtpOptions>
{
    public ValidateOptionsResult Validate(string? name, SmtpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl) ||
            options.PublicBaseUrl.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(origin.Host) ||
            origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0)
            failures.Add("MIR3_PUBLIC_BASE_URL must be an absolute HTTPS origin without credentials, path, query or fragment.");

        if (string.IsNullOrWhiteSpace(options.Host) || options.Host.Any(char.IsWhiteSpace) || Uri.CheckHostName(options.Host) == UriHostNameType.Unknown)
            failures.Add("The SMTP host configuration is invalid.");
        if (options.Port is < 1 or > 65535)
            failures.Add("The SMTP port configuration is invalid.");
        if (!options.UseStartTls)
            failures.Add("SMTP STARTTLS is required.");
        if (!IsAddressOnlyMailbox(options.FromAddress))
            failures.Add("The SMTP sender address configuration is invalid.");
        if (string.IsNullOrWhiteSpace(options.FromName) || options.FromName.Any(char.IsControl))
            failures.Add("The SMTP sender name configuration is invalid.");
        if (options.TimeoutSeconds is < 1 or > 300)
            failures.Add("The SMTP timeout configuration is invalid.");
        if (string.IsNullOrWhiteSpace(options.Username))
            failures.Add($"Environment variable {SmtpOptions.UsernameEnvironmentVariable} is required.");
        if (string.IsNullOrWhiteSpace(options.Password))
            failures.Add($"Environment variable {SmtpOptions.PasswordEnvironmentVariable} is required.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsAddressOnlyMailbox(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || !HasSingleMailboxSeparator(value))
            return false;

        return MailboxAddress.TryParse(value, out var mailbox) &&
               string.IsNullOrEmpty(mailbox.Name) &&
               string.Equals(mailbox.Address, value, StringComparison.Ordinal);
    }

    private static bool HasSingleMailboxSeparator(string value)
    {
        var inQuotedLocalPart = false;
        var escaped = false;
        var separatorIndex = -1;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inQuotedLocalPart && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                inQuotedLocalPart = !inQuotedLocalPart;
                continue;
            }

            if (!inQuotedLocalPart && character == '@')
            {
                if (separatorIndex >= 0)
                    return false;
                separatorIndex = index;
            }
        }

        return !inQuotedLocalPart && separatorIndex > 0 && separatorIndex < value.Length - 1;
    }
}
