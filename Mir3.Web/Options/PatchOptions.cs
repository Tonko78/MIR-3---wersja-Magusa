using Microsoft.Extensions.Options;

namespace Mir3.Web.Options;

public sealed class PatchOptions
{
    public const string SectionName = "Patch";

    public string Directory { get; set; } = "/opt/mir3-web/patch/current";

    public string RequestPath { get; set; } = "/patch";
}

public sealed class PatchOptionsValidator : IValidateOptions<PatchOptions>
{
    public ValidateOptionsResult Validate(string? name, PatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Directory))
        {
            failures.Add("The patch directory is required.");
        }
        else if (!Path.IsPathFullyQualified(options.Directory) ||
                 options.Directory.Split('/', '\\').Any(segment => segment == ".."))
        {
            failures.Add("The patch directory must be an absolute path without parent traversal.");
        }

        if (string.IsNullOrWhiteSpace(options.RequestPath) ||
            !options.RequestPath.StartsWith("/", StringComparison.Ordinal) ||
            options.RequestPath.EndsWith("/", StringComparison.Ordinal) ||
            options.RequestPath.Contains("..", StringComparison.Ordinal) ||
            options.RequestPath.Any(char.IsControl))
        {
            failures.Add("The patch request path must be a single root-relative path segment.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
