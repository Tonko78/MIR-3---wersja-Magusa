using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Mir3.Web.Security;
using Mir3.Web.Localization;
using Mir3.Web.Services;

namespace Mir3.Web.Pages.Admin;

[Authorize(Policy = AdminAuthentication.Policy)]
[ValidateAntiForgeryToken]
public sealed class IndexModel(AdminPortalService adminPortal) : PageModel
{
    public AdminDashboard Dashboard { get; private set; } =
        new([], []);

    public string? Message { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Dashboard = await adminPortal.GetDashboardAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostActivateAsync(
        Guid registrationId,
        string? confirmText,
        CancellationToken cancellationToken)
    {
        return await ApplyActionAsync(registrationId, confirmText, activate: true, cancellationToken);
    }

    public async Task<IActionResult> OnPostDeactivateAsync(
        Guid registrationId,
        string? confirmText,
        CancellationToken cancellationToken)
    {
        return await ApplyActionAsync(registrationId, confirmText, activate: false, cancellationToken);
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(
        Guid registrationId,
        string? newPassword,
        string? confirmPassword,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(newPassword) || !string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            Message = PortalText.Pick("The new passwords must match.", "Nowe hasła muszą być takie same.");
        }
        else
        {
            var actor = User.Identity?.Name ?? "unknown-admin";
            var result = await adminPortal.ResetPasswordAsync(
                registrationId,
                newPassword,
                actor,
                cancellationToken);
            Message = PortalText.AdminMessage(result.Message);
        }

        Dashboard = await adminPortal.GetDashboardAsync(cancellationToken);
        ViewData["LanguageReturnUrl"] = "/Admin";
        return Page();
    }

    private async Task<IActionResult> ApplyActionAsync(
        Guid registrationId,
        string? confirmText,
        bool activate,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(confirmText, "CONFIRM", StringComparison.Ordinal))
        {
            Message = PortalText.Pick("Type CONFIRM to apply this action.", "Wpisz CONFIRM, aby wykonać tę operację.");
        }
        else
        {
            var actor = User.Identity?.Name ?? "unknown-admin";
            var result = await adminPortal.ApplyRegistrationActionAsync(
                registrationId,
                activate,
                actor,
                cancellationToken);
            Message = PortalText.AdminMessage(result.Message);
        }

        Dashboard = await adminPortal.GetDashboardAsync(cancellationToken);
        ViewData["LanguageReturnUrl"] = "/Admin";
        return Page();
    }
}
