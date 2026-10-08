using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Mir3.Web.Services;

namespace Mir3.Web.Pages;

public sealed class IndexModel(DownloadArtifactCatalog downloads) : PageModel
{
    public DownloadMetadata Download { get; private set; } = DownloadMetadata.Unavailable(string.Empty, string.Empty);

    public void OnGet()
    {
        Download = downloads.Launcher.GetMetadata();
    }
}
