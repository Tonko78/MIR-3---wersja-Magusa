using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Mir3.Web.Security;
using Mir3.Web.Localization;
using Mir3.Web.Services;

namespace Mir3.Web.Pages.Admin;

[Authorize(Policy = AdminAuthentication.Policy)]
[ValidateAntiForgeryToken]
public sealed class SettingsModel(AdminPortalService adminPortal) : PageModel
{
    [BindProperty]
    public bool AutoActivate { get; set; }

    [BindProperty]
    public string? ConfirmText { get; set; }

    public string? Message { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        AutoActivate = await adminPortal.GetAutoActivateAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            ModelState.Clear();
            return Page();
        }

        if (!string.Equals(ConfirmText, "CONFIRM", StringComparison.Ordinal))
        {
            Message = PortalText.Pick("Type CONFIRM to save settings.", "Wpisz CONFIRM, aby zapisać ustawienia.");
            AutoActivate = await adminPortal.GetAutoActivateAsync(cancellationToken);
            return Page();
        }

        var actor = User.Identity?.Name ?? "unknown-admin";
        var result = await adminPortal.UpdateSettingsAsync(AutoActivate, actor, cancellationToken);
        Message = PortalText.AdminMessage(result.Message);
        AutoActivate = await adminPortal.GetAutoActivateAsync(cancellationToken);
        return Page();
    }
}
