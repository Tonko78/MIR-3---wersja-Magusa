using System.Globalization;
using Mir3.Web.Domain;

namespace Mir3.Web.Localization;

/// <summary>
/// Selects the visitor-facing text for the current request UI culture.
/// Returns the Polish variant only when the resolved UI culture is Polish;
/// every other culture (including invariant/unknown) falls back to English.
/// </summary>
public static class PortalText
{
    private static bool IsPolish => string.Equals(
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "pl", StringComparison.Ordinal);

    public static string Pick(string english, string polish) =>
        IsPolish ? polish : english;

    public static string AdminStatusLabel(RegistrationStatus status, string englishLabel) =>
        Pick(englishLabel, status switch
        {
            RegistrationStatus.PendingEmail => "Oczekuje na e-mail",
            RegistrationStatus.AwaitingAdmin => "Oczekuje na administratora",
            RegistrationStatus.QueuePending => "W kolejce",
            RegistrationStatus.Active => "Aktywne",
            RegistrationStatus.Disabled => "Wyłączone",
            RegistrationStatus.Failed => "Błąd",
            _ => "Nieznany"
        });

    // Audit events retain their stable machine codes in storage; localize
    // only the human-readable dashboard cells and keep unknown codes intact.
    public static string AdminAuditAction(string code) => Pick(code, code switch
    {
        "Admin.Registration.Activate" => "Aktywacja konta",
        "Admin.Registration.Deactivate" => "Wyłączenie konta",
        "Admin.Registration.ResetPassword" => "Reset hasła",
        "Admin.Settings.AutoActivate" => "Zmiana automatycznej aktywacji",
        "registration.queue-result" => "Wynik kolejki rejestracji",
        _ => code
    });

    // Keep English service contracts, security decisions and audit codes unchanged.
    public static string AdminMessage(string? english)
    {
        if (english is null || !IsPolish) return english ?? string.Empty;
        return english switch
        {
            "The username or password is incorrect." => "Nieprawidłowa nazwa użytkownika lub hasło.",
            "The registration could not be found." => "Nie znaleziono rejestracji.",
            "This registration cannot be activated in its current state." => "W obecnym stanie nie można aktywować tej rejestracji.",
            "This registration cannot be deactivated in its current state." => "W obecnym stanie nie można wyłączyć tej rejestracji.",
            "This registration cannot have its password reset in its current state." => "W obecnym stanie nie można zresetować hasła tej rejestracji.",
            "Account activation queued." => "Aktywacja konta została dodana do kolejki.",
            "Account deactivation queued." => "Wyłączenie konta zostało dodane do kolejki.",
            "Password reset queued. The plaintext password is never stored or shown by the portal." => "Reset hasła został dodany do kolejki. Portal nie przechowuje ani nie wyświetla hasła w postaci jawnej.",
            "Portal settings are unavailable." => "Ustawienia portalu są niedostępne.",
            "Settings saved." => "Ustawienia zapisano.",
            _ when english.StartsWith("Password must be between ", StringComparison.Ordinal) => "Hasło musi mieć od 5 do 15 znaków i nie może zawierać białych znaków.",
            _ => english
        };
    }
}
