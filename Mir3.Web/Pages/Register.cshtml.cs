using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Mir3.Web.Security;
using Mir3.Web.Services;
using Mir3.Web.Localization;

namespace Mir3.Web.Pages;

[ValidateAntiForgeryToken]
[EnableRateLimiting("registration")]
public sealed class RegisterModel(IRegistrationService registrations) : PageModel
{
    [BindProperty]
    public RegisterInput Input { get; set; } = new();

    public string? ResultMessage { get; private set; }

    public string ResultMessageCssClass { get; private set; } = "status-note--info";

    public string ResultMessageIcon { get; private set; } = "i";

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TranslateValidationErrors();
            return Page();
        }

        var sourceIp = HttpContext.Connection.RemoteIpAddress;
        if (sourceIp is null)
        {
            return StatusCode(StatusCodes.Status400BadRequest);
        }

        var result = await registrations.RegisterAsync(
            Input.Email,
            Input.Password,
            sourceIp,
            cancellationToken);
        Input.Password = string.Empty;
        Input.ConfirmPassword = string.Empty;
        ResultMessage = result.UserMessage == RegistrationMessages.Created
            ? PortalText.Pick(RegistrationMessages.Created, "Otrzymaliśmy zgłoszenie rejestracyjne. Jeśli ten adres można zarejestrować, wyślemy e-mail weryfikacyjny. Sprawdź skrzynkę i postępuj według instrukcji. Jeśli wiadomość nie dotrze, spróbuj ponownie później.")
            : PortalText.Pick(result.UserMessage, "Nie udało się przetworzyć zgłoszenia. Spróbuj ponownie później.");

        return Page();
    }

    private void TranslateValidationErrors()
    {
        if (PortalText.Pick("en", "pl") != "pl") return;
        foreach (var pair in ModelState.ToArray())
        {
            if (pair.Value?.Errors.Count is not > 0) continue;
            var key = pair.Key;
            pair.Value.Errors.Clear();
            var message = key switch
            {
                "Input.Email" when string.IsNullOrWhiteSpace(Input.Email) => "Adres e-mail jest wymagany.",
                "Input.Email" when Input.Email.Length > 320 => "Adres e-mail jest zbyt długi.",
                "Input.Email" => "Podaj poprawny adres e-mail.",
                "Input.Password" when string.IsNullOrEmpty(Input.Password) => "Hasło jest wymagane.",
                "Input.Password" when Input.Password.Length < GamePasswordHasher.MinimumPasswordLength || Input.Password.Length > GamePasswordHasher.MaximumPasswordLength => $"Hasło musi mieć od {GamePasswordHasher.MinimumPasswordLength} do {GamePasswordHasher.MaximumPasswordLength} znaków.",
                "Input.Password" => "Hasło nie może zawierać białych znaków.",
                "Input.ConfirmPassword" when string.IsNullOrEmpty(Input.ConfirmPassword) => "Potwierdź hasło.",
                "Input.ConfirmPassword" => "Hasła nie są takie same.",
                "Input.AcceptRules" => "Musisz zaakceptować regulamin serwera.",
                _ => "Nieprawidłowa wartość formularza."
            };
            ModelState.AddModelError(key, message);
        }
    }

    public sealed class RegisterInput
    {
        [Required, EmailAddress, StringLength(320)]
        public string Email { get; set; } = string.Empty;

        [Required]
        [StringLength(GamePasswordHasher.MaximumPasswordLength, MinimumLength = GamePasswordHasher.MinimumPasswordLength)]
        [RegularExpression("^\\S+$", ErrorMessage = "Password cannot contain whitespace.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required, DataType(DataType.Password), Compare(nameof(Password))]
        public string ConfirmPassword { get; set; } = string.Empty;

        [Range(typeof(bool), "true", "true", ErrorMessage = "You must accept the server rules.")]
        public bool AcceptRules { get; set; }
    }
}
