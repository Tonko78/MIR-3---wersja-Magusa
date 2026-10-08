using Microsoft.Extensions.Options;
using Mir3.Web.Options;

namespace Mir3.Web.Services;

// Routes select a known service; request data never becomes a filesystem path.
public sealed class DownloadArtifactCatalog
{
    public DownloadArtifactCatalog(
        IOptions<DownloadOptions> options,
        DownloadMetadataService legacy,
        ILogger<DownloadMetadataService> logger)
    {
        var configured = options.Value;
        Legacy = legacy;
        Zip = Create(configured, configured.ZipFileName, logger);
        Launcher = Create(configured, configured.LauncherFileName, logger);
    }

    public DownloadMetadataService Legacy { get; }
    public DownloadMetadataService Zip { get; }
    public DownloadMetadataService Launcher { get; }

    private static DownloadMetadataService Create(
        DownloadOptions configured,
        string fileName,
        ILogger<DownloadMetadataService> logger) =>
        new(Microsoft.Extensions.Options.Options.Create(new DownloadOptions
        {
            Directory = configured.Directory,
            FileName = fileName,
            ZipFileName = configured.ZipFileName,
            LauncherFileName = configured.LauncherFileName,
            Version = configured.Version
        }), logger);
}
