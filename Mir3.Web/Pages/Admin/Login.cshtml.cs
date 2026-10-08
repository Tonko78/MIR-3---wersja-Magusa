using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Mir3.Web.Security;
using Mir3.Web.Localization;
using Mir3.Web.Services;

namespace Mir3.Web.Pages.Admin;

[AllowAnonymous]
[EnableRateLimiting("admin-login")]
[ValidateAntiForgeryToken]
public sealed class LoginModel(AdminPortalService adminPortal) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            ErrorMessage = PortalText.Pick("The username or password is incorrect.", "Nieprawidłowa nazwa użytkownika lub hasło.");
            return Page();
        }

        var result = await adminPortal.AuthenticateAsync(Input.Username, Input.Password, cancellationToken);
        if (!result.Succeeded)
        {
            ErrorMessage = PortalText.AdminMessage(result.Message);
            return Page();
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, result.Username!),
            new Claim(ClaimTypes.NameIdentifier, result.Username!)
        ],
        AdminAuthentication.Scheme));
        await HttpContext.SignInAsync(AdminAuthentication.Scheme, principal);
        return RedirectToPage("/Admin/Index");
    }

    public sealed class LoginInput
    {
        [Required]
        public string Username { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;
    }
}
