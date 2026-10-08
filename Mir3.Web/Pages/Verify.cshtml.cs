using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Mir3.Web.Services;
using Mir3.Web.Localization;

namespace Mir3.Web.Pages;

[ValidateAntiForgeryToken]
[EnableRateLimiting("verification-resend")]
public sealed class VerifyModel(IRegistrationService registrations) : PageModel
{
    public string Message { get; private set; } = PortalText.Pick(RegistrationMessages.VerificationInvalid, "Ten link weryfikacyjny jest nieprawidłowy, wygasł lub został już wykorzystany.");
    public Guid? RegistrationReference { get; private set; }
    public bool CanResend { get; private set; }

    public async Task OnGetAsync(string? token, CancellationToken cancellationToken)
    {
        var result = await registrations.VerifyAsync(token, cancellationToken);
        Message = result.Outcome == RegistrationVerificationOutcome.Verified
            ? PortalText.Pick(RegistrationMessages.VerificationSucceeded, "Twój adres e-mail został zweryfikowany.")
            : PortalText.Pick(RegistrationMessages.VerificationInvalid, "Ten link weryfikacyjny jest nieprawidłowy, wygasł lub został już wykorzystany.");
        RegistrationReference = result.RegistrationReference;
        CanResend = result.CanResend;
        // Do not replay a consumed one-time verification token on language switch.
        if (result.Outcome == RegistrationVerificationOutcome.Verified && RegistrationReference is { } reference && PageContext?.ViewData is { } viewData)
            viewData["LanguageReturnUrl"] = $"/Status?registration={reference:D}";
    }

    public async Task<IActionResult> OnPostResendAsync(
        Guid registration,
        CancellationToken cancellationToken)
    {
        await registrations.ResendVerificationAsync(registration, cancellationToken);
        Message = PortalText.Pick(RegistrationMessages.ResendAccepted, "Jeśli dla tej rejestracji można wysłać kolejną wiadomość, wyślemy e-mail weryfikacyjny.");
        RegistrationReference = registration == Guid.Empty ? null : registration;
        CanResend = RegistrationReference is not null;
        if (RegistrationReference is { } reference && PageContext?.ViewData is { } viewData)
            viewData["LanguageReturnUrl"] = $"/Status?registration={reference:D}";
        return Page();
    }
}
