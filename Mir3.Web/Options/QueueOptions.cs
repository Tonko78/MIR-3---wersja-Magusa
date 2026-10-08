using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Mir3.Web.Options;

public sealed class QueueOptions
{
    public const string SectionName = "Queue";
    public const string HmacEnvironmentVariable = "MIR3_ACCOUNT_QUEUE_HMAC_KEY";

    public string RootPath { get; set; } = "/opt/zircon/account-queue";

    public int ResultPollSeconds { get; set; } = 2;

    public string? HmacKeyBase64 { get; set; }
}

public sealed class QueueOptionsSetup(
    IConfiguration configuration,
    Func<string, string?>? readEnvironmentVariable = null) : IConfigureOptions<QueueOptions>
{
    private readonly Func<string, string?> _readEnvironmentVariable =
        readEnvironmentVariable ?? Environment.GetEnvironmentVariable;

    public void Configure(QueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        configuration.GetSection(QueueOptions.SectionName).Bind(options);
        options.HmacKeyBase64 = _readEnvironmentVariable(QueueOptions.HmacEnvironmentVariable);
    }
}

public sealed class QueueOptionsValidator : IValidateOptions<QueueOptions>
{
    private readonly Func<bool> _isLinux;

    public QueueOptionsValidator()
        : this(OperatingSystem.IsLinux)
    {
    }

    internal QueueOptionsValidator(Func<bool> isLinux)
    {
        ArgumentNullException.ThrowIfNull(isLinux);
        _isLinux = isLinux;
    }

    public ValidateOptionsResult Validate(string? name, QueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (!_isLinux())
        {
            failures.Add("The account queue integration is supported only on Linux.");
            return ValidateOptionsResult.Fail(failures);
        }

        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            failures.Add("The account queue root path is required.");
        }
        else if (!Path.IsPathFullyQualified(options.RootPath))
        {
            failures.Add("The account queue root path must be absolute.");
        }

        if (options.ResultPollSeconds <= 0)
        {
            failures.Add("The account queue result poll interval must be positive.");
        }

        byte[]? key = null;
        try
        {
            if (string.IsNullOrWhiteSpace(options.HmacKeyBase64))
            {
                failures.Add($"Environment variable {QueueOptions.HmacEnvironmentVariable} is required.");
            }
            else
            {
                try
                {
                    key = Convert.FromBase64String(options.HmacKeyBase64);
                }
                catch (FormatException)
                {
                    failures.Add("The account queue HMAC key must be valid Base64.");
                }

                if (key is not null && key.Length != 32)
                {
                    failures.Add("The account queue HMAC key must decode to exactly 32 bytes.");
                }
            }
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
