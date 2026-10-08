using Microsoft.Extensions.Options;

namespace Mir3.Web.Options;

public sealed class DownloadOptions
{
    public const string SectionName = "Download";

    public string Directory { get; set; } = "/opt/mir3-web/downloads";

    public string FileName { get; set; } = "Mir3-Zircon-Client-2026-09-22.7z";
    public string ZipFileName { get; set; } = "Mir3-Zircon-Client-2026-09-22.zip";
    public string LauncherFileName { get; set; } = "Mir3-Zircon-Launcher-Setup.exe";

    public string Version { get; set; } = "4cc883d-2026.09.22";
}

public sealed class DownloadOptionsValidator : IValidateOptions<DownloadOptions>
{
    public ValidateOptionsResult Validate(string? name, DownloadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Directory))
        {
            failures.Add("The download directory is required.");
        }
        else if (!Path.IsPathFullyQualified(options.Directory))
        {
            failures.Add("The download directory must be absolute.");
        }

        if (string.IsNullOrWhiteSpace(options.FileName))
        {
            failures.Add("The download file name is required.");
        }
        else if (Path.IsPathRooted(options.FileName) ||
                 !string.Equals(Path.GetFileName(options.FileName), options.FileName, StringComparison.Ordinal) ||
                 options.FileName is "." or ".." ||
                 options.FileName.Contains('/') ||
                 options.FileName.Contains('\\') ||
                 options.FileName.Any(char.IsControl))
        {
            failures.Add("The download file name must be a single safe file name.");
        }

        if (string.IsNullOrWhiteSpace(options.Version) || options.Version.Any(char.IsControl))
        {
            failures.Add("The download version is required and must not contain control characters.");
        }

        if (!SafeArtifactName(options.ZipFileName, ".zip"))
            failures.Add("The ZIP download file name must be a single safe .zip name.");
        if (!SafeArtifactName(options.LauncherFileName, ".exe"))
            failures.Add("The launcher download file name must be a single safe .exe name.");
        if (new[] { options.FileName, options.ZipFileName, options.LauncherFileName }
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            failures.Add("Download file names must be distinct.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool SafeArtifactName(string? fileName, string suffix) =>
        fileName is { Length: > 0 and <= 180 } &&
        fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
        fileName.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_') &&
        fileName[0] != '.' && fileName is not "." and not "..";
}
