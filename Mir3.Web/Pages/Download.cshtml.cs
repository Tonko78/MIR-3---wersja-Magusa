using Microsoft.AspNetCore.Mvc.RazorPages;
using Mir3.Web.Services;

namespace Mir3.Web.Pages;

public sealed class DownloadModel(DownloadArtifactCatalog downloads) : PageModel
{
    public DownloadMetadata Launcher { get; private set; } = DownloadMetadata.Unavailable(string.Empty, string.Empty);
    public DownloadMetadata Zip { get; private set; } = DownloadMetadata.Unavailable(string.Empty, string.Empty);
    public DownloadMetadata Legacy { get; private set; } = DownloadMetadata.Unavailable(string.Empty, string.Empty);

    public void OnGet()
    {
        Launcher = downloads.Launcher.GetMetadata();
        Zip = downloads.Zip.GetMetadata();
        Legacy = downloads.Legacy.GetMetadata();
    }
}
