using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Mir3.Web.Services;
using Mir3.Web.Localization;

namespace Mir3.Web.Pages;

[EnableRateLimiting("status")]
public sealed class StatusModel(IRegistrationService registrations) : PageModel
{
    private static readonly IReadOnlyDictionary<string, StatusPresentation> Presentations =
        new Dictionary<string, StatusPresentation>(StringComparer.Ordinal)
        {
            ["Waiting for e-mail verification"] = new("status-badge--pending", "✉"),
            ["Waiting for approval"] = new("status-badge--pending", "…"),
            ["Processing"] = new("status-badge--processing", "↻"),
            ["Active"] = new("status-badge--active", "✓"),
            ["Unavailable"] = new("status-badge--unavailable", "!"),
            ["Processing delayed"] = new("status-badge--delayed", "!")
        };

    public string PublicStatus { get; private set; } = PortalText.Pick("Unavailable", "Niedostępny");
    public string Message { get; private set; } = PortalText.Pick(RegistrationMessages.StatusUnavailable, "Status rejestracji jest niedostępny.");
    public string? EmailAddress { get; private set; }
    public string StatusBadgeClass { get; private set; } = "status-badge--unavailable";
    public string StatusIcon { get; private set; } = "!";

    public async Task OnGetAsync(Guid registration, CancellationToken cancellationToken)
    {
        var result = await registrations.GetStatusAsync(registration, cancellationToken);
        if (!Presentations.TryGetValue(result.PublicStatus, out var presentation))
        {
            PublicStatus = PortalText.Pick("Unavailable", "Niedostępny");
            Message = PortalText.Pick(RegistrationMessages.StatusUnavailable, "Status rejestracji jest niedostępny.");
            StatusBadgeClass = "status-badge--unavailable";
            StatusIcon = "!";
            return;
        }

        PublicStatus = PortalText.Pick(result.PublicStatus, result.PublicStatus switch
        {
            "Waiting for e-mail verification" => "Oczekiwanie na weryfikację e-mail",
            "Waiting for approval" => "Oczekiwanie na zatwierdzenie",
            "Processing" => "Przetwarzanie",
            "Active" => "Aktywne",
            "Unavailable" => "Niedostępny",
            "Processing delayed" => "Opóźnienie przetwarzania",
            _ => "Niedostępny"
        });
        Message = result.UserMessage == $"Registration status: {result.PublicStatus}."
            ? PortalText.Pick(result.UserMessage, $"Status rejestracji: {PublicStatus}.")
            : result.UserMessage == RegistrationMessages.StatusUnavailable
                ? PortalText.Pick(RegistrationMessages.StatusUnavailable, "Status rejestracji jest niedostępny.")
                : PortalText.Pick(result.UserMessage, "Status rejestracji jest niedostępny.");
        EmailAddress = result.Email;
        StatusBadgeClass = presentation.CssClass;
        StatusIcon = presentation.Icon;
    }

    private sealed record StatusPresentation(string CssClass, string Icon);
}
