using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mir3.Web.Options;
using Mir3.Web.Services;

namespace Mir3.Web.Tests;

/// <summary>
/// Task 2A: visitor-facing Index, Download, shared layout and the HTML 503
/// download-unavailable page render Polish for Polish requests and retain the
/// exact English copy and download routes otherwise.
/// </summary>
public sealed class PublicPageLocalizationTests : IDisposable
{
    private readonly PageFactory _factory = new(createArchive: true);

    [Fact]
    public async Task Switching_language_after_one_time_verification_keeps_private_status_link()
    {
        var reference = Guid.NewGuid();
        var registrations = new OneTimeVerificationService(reference);
        using var factory = new PageFactory(createArchive: true, registrations);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var verified = await client.GetAsync("/Verify?token=one-time-value");
        var html = await verified.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        Assert.Contains(RegistrationMessages.VerificationSucceeded, html, StringComparison.Ordinal);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(token.Success);
        var returnUrl = Regex.Match(html, "name=\"returnUrl\"[^>]*value=\"([^\"]+)\"");
        Assert.True(returnUrl.Success);
        Assert.Equal($"/Status?registration={reference:D}", WebUtility.HtmlDecode(returnUrl.Groups[1].Value));

        using var selected = await client.PostAsync("/language", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
            ["returnUrl"] = WebUtility.HtmlDecode(returnUrl.Groups[1].Value),
            ["language"] = "pl-PL"
        }));
        Assert.Equal(HttpStatusCode.Redirect, selected.StatusCode);
        Assert.Equal($"/Status?registration={reference:D}", selected.Headers.Location?.ToString());
        using var next = await client.GetAsync(selected.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Contains("Status rejestracji", await next.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(1, registrations.VerificationCalls);
    }

    [Fact]
    public async Task Polish_home_renders_polish_copy_metadata_units_and_exact_links()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("Accept-Language", "pl-PL,pl;q=0.9");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<html lang=\"pl\"", body, StringComparison.Ordinal);
        Assert.Contains("Wróć na pogranicze.", body, StringComparison.Ordinal);
        Assert.Contains("Załóż konto", body, StringComparison.Ordinal);
        Assert.Contains("Pobierz instalator launchera", body, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Akcje portalu\"", body, StringComparison.Ordinal);
        Assert.Contains("Dostępny do pobrania", body, StringComparison.Ordinal);
        Assert.Contains("bajtów", body, StringComparison.Ordinal);
        Assert.Contains("<dt>Wersja</dt>", body, StringComparison.Ordinal);
        Assert.Contains("<dt>Rozmiar</dt>", body, StringComparison.Ordinal);
        Assert.Contains("Rejestracja", body, StringComparison.Ordinal);
        Assert.Contains("Weryfikacja", body, StringComparison.Ordinal);
        Assert.Contains("Wejdź do Mir", body, StringComparison.Ordinal);
        Assert.Contains("href=\"/download/launcher\"", body, StringComparison.Ordinal);
        // Image alt text is translated too.
        Assert.Contains("alt=\"Ekran logowania Legend of Mir 3", body, StringComparison.Ordinal);
        // No stray English visitor copy remains.
        Assert.DoesNotContain("Return to the frontier.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Portal actions", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Available for download", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task English_home_retains_exact_english_copy_units_and_links()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<html lang=\"en\"", body, StringComparison.Ordinal);
        Assert.Contains("Return to the frontier.", body, StringComparison.Ordinal);
        Assert.Contains("Create an account", body, StringComparison.Ordinal);
        Assert.Contains("Download launcher installer", body, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Portal actions\"", body, StringComparison.Ordinal);
        Assert.Contains("Available for download", body, StringComparison.Ordinal);
        Assert.Contains(" bytes", body, StringComparison.Ordinal);
        Assert.Contains("<dt>Version</dt>", body, StringComparison.Ordinal);
        Assert.Contains("<dt>Size</dt>", body, StringComparison.Ordinal);
        Assert.Contains("href=\"/download/launcher\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("bajtów", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Polish_download_page_renders_polish_warnings_and_exact_links()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/download");
        request.Headers.TryAddWithoutValidation("Accept-Language", "pl-PL,pl;q=0.9");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<html lang=\"pl\"", body, StringComparison.Ordinal);
        Assert.Contains("Wybierz swoją drogę.", body, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Opcje pobierania\"", body, StringComparison.Ordinal);
        // Exact download routes are unchanged.
        Assert.Contains("href=\"/download/launcher\"", body, StringComparison.Ordinal);
        Assert.Contains("href=\"/download/client\"", body, StringComparison.Ordinal);
        Assert.Contains("href=\"/download/client-7z\"", body, StringComparison.Ordinal);
        Assert.Contains("Pobierz instalator Windows", body, StringComparison.Ordinal);
        Assert.Contains("Pobierz klienta ZIP", body, StringComparison.Ordinal);
        Assert.Contains("Pobierz starsze 7z", body, StringComparison.Ordinal);
        Assert.Contains("bajtów · Wersja", body, StringComparison.Ordinal);
        Assert.Contains("bajtów · SHA-256", body, StringComparison.Ordinal);
        // Unsigned-installer security warning stays explicit in Polish.
        Assert.Contains("SmartScreen", body, StringComparison.Ordinal);
        Assert.Contains("sumę kontrolną", body, StringComparison.Ordinal);
        Assert.Contains("nie jest podpisany", body, StringComparison.Ordinal);
        // Manual cleanup note and ZIP-preview warning are translated.
        Assert.Contains("%LOCALAPPDATA%", body, StringComparison.Ordinal);
        Assert.Contains("Odinstalowanie launchera", body, StringComparison.Ordinal);
        Assert.Contains("Wypakuj całe archiwum", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Choose your way in.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Download options", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task English_download_page_retains_exact_english_warnings_and_links()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/download");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<html lang=\"en\"", body, StringComparison.Ordinal);
        Assert.Contains("Choose your way in.", body, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Download options\"", body, StringComparison.Ordinal);
        Assert.Contains("href=\"/download/launcher\"", body, StringComparison.Ordinal);
        Assert.Contains("href=\"/download/client\"", body, StringComparison.Ordinal);
        Assert.Contains("href=\"/download/client-7z\"", body, StringComparison.Ordinal);
        Assert.Contains("bytes · Version", body, StringComparison.Ordinal);
        Assert.Contains("SmartScreen", body, StringComparison.Ordinal);
        Assert.Contains("checksum", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("without launcher", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%LOCALAPPDATA%", body, StringComparison.Ordinal);
        Assert.Contains("Uninstall", body, StringComparison.Ordinal);
        Assert.DoesNotContain("bajtów", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Polish_layout_renders_polish_navigation_footer_and_meta_description()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("Accept-Language", "pl-PL,pl;q=0.9");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("aria-label=\"Nawigacja główna\"", body, StringComparison.Ordinal);
        Assert.Contains(">Strona główna<", body, StringComparison.Ordinal);
        Assert.Contains(">Rejestracja<", body, StringComparison.Ordinal);
        Assert.Contains(">Pobieranie<", body, StringComparison.Ordinal);
        Assert.Contains(">Prywatność<", body, StringComparison.Ordinal);
        Assert.Contains("content=\"Portal rejestracji i klienta Mir3 Zircon\"", body, StringComparison.Ordinal);
        Assert.Contains("Przełącz nawigację", body, StringComparison.Ordinal);
        // The language switch stays bilingual and the wiki link is untouched.
        Assert.Contains("aria-label=\"Language / Język\"", body, StringComparison.Ordinal);
        Assert.Contains("href=\"https://mir3wiki.swoojeff.online/\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain(">Home<", body, StringComparison.Ordinal);
        Assert.DoesNotContain(">Register<", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task English_layout_retains_exact_english_navigation_footer_and_meta_description()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("aria-label=\"Primary navigation\"", body, StringComparison.Ordinal);
        Assert.Contains(">Home<", body, StringComparison.Ordinal);
        Assert.Contains(">Register<", body, StringComparison.Ordinal);
        Assert.Contains(">Download<", body, StringComparison.Ordinal);
        Assert.Contains(">Privacy<", body, StringComparison.Ordinal);
        Assert.Contains("content=\"Mir3 Zircon registration and client portal\"", body, StringComparison.Ordinal);
        Assert.Contains("Toggle navigation", body, StringComparison.Ordinal);
        Assert.DoesNotContain(">Pobieranie<", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/Register", "Utwórz konto")]
    [InlineData("/Verify", "Weryfikacja adresu e-mail")]
    [InlineData("/Status", "Status rejestracji")]
    [InlineData("/Privacy", "Polityka prywatności")]
    [InlineData("/Error", "Wystąpił nieoczekiwany błąd")]
    public async Task Polish_public_account_pages_have_polish_headings(string path, string heading)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Accept-Language", "pl-PL");
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<html lang=\"pl\">", body, StringComparison.Ordinal);
        Assert.Contains(heading, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/Register", "Create your Mir3 account.")]
    [InlineData("/Verify", "E-mail verification")]
    [InlineData("/Status", "Registration status")]
    [InlineData("/Privacy", "Privacy policy")]
    [InlineData("/Error", "We hit an unexpected fault.")]
    public async Task English_account_pages_keep_english_fallback(string path, string heading)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<html lang=\"en\">", html, StringComparison.Ordinal);
        Assert.Contains(heading, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task English_forms_retain_client_validation_hints_without_english_hints_in_polish()
    {
        using var client = _factory.CreateClient();
        var englishRegister = await client.GetStringAsync("/Register");
        var englishLogin = await client.GetStringAsync("/Admin/Login");
        Assert.Contains("data-val-required=", englishRegister, StringComparison.Ordinal);
        Assert.Contains("data-val-required=", englishLogin, StringComparison.Ordinal);

        using var polishRegisterRequest = new HttpRequestMessage(HttpMethod.Get, "/Register");
        polishRegisterRequest.Headers.TryAddWithoutValidation("Accept-Language", "pl-PL");
        using var polishRegisterResponse = await client.SendAsync(polishRegisterRequest);
        Assert.DoesNotContain("data-val-required=", await polishRegisterResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Polish_invalid_registration_shows_polish_validation_without_creating_account()
    {
        using var client = _factory.CreateClient();
        using var get = new HttpRequestMessage(HttpMethod.Get, "/Register");
        get.Headers.TryAddWithoutValidation("Accept-Language", "pl-PL");
        using var page = await client.SendAsync(get);
        var html = await page.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Registration form must carry an antiforgery token");
        using var post = new HttpRequestMessage(HttpMethod.Post, "/Register");
        post.Headers.TryAddWithoutValidation("Accept-Language", "pl-PL");
        post.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
            ["Input.Email"] = "invalid-email",
            ["Input.Password"] = "abc",
            ["Input.ConfirmPassword"] = "bad",
            ["Input.AcceptRules"] = "false"
        });
        using var response = await client.SendAsync(post);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Podaj poprawny adres e-mail", body, StringComparison.Ordinal);
        Assert.Contains("Hasło", body, StringComparison.Ordinal);
        Assert.Contains("zaakceptować regulamin", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Password cannot contain whitespace", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Polish_registration_validation_encodes_untrusted_html_in_form_values()
    {
        using var client = _factory.CreateClient();
        using var get = new HttpRequestMessage(HttpMethod.Get, "/Register");
        get.Headers.TryAddWithoutValidation("Accept-Language", "pl-PL");
        using var page = await client.SendAsync(get);
        var html = await page.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        using var post = new HttpRequestMessage(HttpMethod.Post, "/Register")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
                ["Input.Email"] = "<img src=x onerror=alert(1)>",
                ["Input.Password"] = "abc",
                ["Input.ConfirmPassword"] = "abc",
                ["Input.AcceptRules"] = "false"
            })
        };
        post.Headers.TryAddWithoutValidation("Accept-Language", "pl-PL");
        using var response = await client.SendAsync(post);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("value=\"<img", body, StringComparison.Ordinal);
        Assert.Contains("&lt;img", body, StringComparison.Ordinal);
        Assert.Contains("Podaj poprawny adres e-mail", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Polish_home_installer_unavailable_branch_links_to_downloads_page()
    {
        using var factory = new PageFactory(createArchive: false);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("Accept-Language", "pl-PL,pl;q=0.9");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The installer-unavailable notice keeps a direct inline link to the
        // Downloads page (not just plain text), localized for Polish.
        Assert.Contains("Zajrzyj na stronę <a href=\"/download\">Pobieranie</a>, aby pobrać archiwum ręcznie.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task English_home_installer_unavailable_branch_links_to_downloads_page()
    {
        using var factory = new PageFactory(createArchive: false);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("See the <a href=\"/download\">Downloads page</a> for manual archive options.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Polish_download_unavailable_page_is_polish_503()
    {
        using var factory = new PageFactory(createArchive: false);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/download/client-7z");
        request.Headers.TryAddWithoutValidation("Accept-Language", "pl-PL,pl;q=0.9");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("<html lang=\"pl\">", body, StringComparison.Ordinal);
        Assert.Contains("Pobieranie klienta niedostępne", body, StringComparison.Ordinal);
        Assert.Contains("Wróć do portalu", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Client download unavailable", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task English_download_unavailable_page_is_english_503()
    {
        using var factory = new PageFactory(createArchive: false);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/download/client-7z");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("<html lang=\"en\">", body, StringComparison.Ordinal);
        Assert.Contains("Client download unavailable", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Return to the portal", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Pobieranie klienta niedostępne", body, StringComparison.Ordinal);
    }

    public void Dispose() => _factory.Dispose();

    private sealed class OneTimeVerificationService(Guid reference) : IRegistrationService
    {
        public int VerificationCalls { get; private set; }

        public Task<RegistrationVerificationResult> VerifyAsync(string? token, CancellationToken cancellationToken = default)
        {
            VerificationCalls++;
            return Task.FromResult(VerificationCalls == 1
                ? new RegistrationVerificationResult(RegistrationVerificationOutcome.Verified, RegistrationMessages.VerificationSucceeded, reference)
                : new RegistrationVerificationResult(RegistrationVerificationOutcome.Invalid, RegistrationMessages.VerificationInvalid));
        }

        public Task<RegistrationStatusResult> GetStatusAsync(Guid registrationReference, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RegistrationStatusResult(RegistrationStatusOutcome.Found, "Active", "Registration status: Active.", "player@example.test"));

        public Task<RegistrationCreateResult> RegisterAsync(string email, string password, IPAddress sourceIp, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ResendVerificationAsync(Guid registrationReference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class PageFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"mir3-page-localization-tests-{Guid.NewGuid():N}");

        private readonly IRegistrationService? _registrationStub;

        public PageFactory(bool createArchive, IRegistrationService? registrationStub = null)
        {
            _registrationStub = registrationStub;
            Directory.CreateDirectory(_root);
            if (createArchive)
            {
                File.WriteAllBytes(Path.Combine(_root, "Mir3-Zircon-Launcher-Setup.exe"), [1, 2, 3]);
                File.WriteAllBytes(Path.Combine(_root, "Mir3-Zircon-Client-2026-09-22.zip"), [4, 5, 6]);
                File.WriteAllBytes(Path.Combine(_root, "Mir3-Zircon-Client-2026-09-22.7z"), [7, 8, 9]);
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var queue = Path.Combine(_root, "queue");
            var locks = Path.Combine(_root, "locks");
            builder.UseEnvironment("Development");
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Portal"] = $"Data Source={Path.Combine(_root, "portal.db")}",
                ["VerificationDelivery:LockDirectory"] = locks,
                ["Queue:RootPath"] = queue,
                ["Queue:ResultPollSeconds"] = "2",
                ["MIR3_PUBLIC_BASE_URL"] = "https://portal.example.invalid",
                ["Smtp:Host"] = "smtp.example.invalid",
                ["Smtp:Port"] = "587",
                ["Smtp:UseStartTls"] = "true",
                ["Smtp:FromAddress"] = "mir3@example.invalid",
                ["Smtp:FromName"] = "Mir3",
                ["Smtp:TimeoutSeconds"] = "20",
                ["MIR3_ADMIN_USERNAME"] = "test-bootstrap-admin",
                ["MIR3_ADMIN_INITIAL_PASSWORD"] = "TestBootstrapPass7!",
                ["Download:Directory"] = _root,
                ["Download:FileName"] = "Mir3-Zircon-Client-2026-09-22.7z",
                ["Download:Version"] = "4cc883d-2026.09.22"
            };
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
            {
                if (_registrationStub is not null)
                    services.Replace(ServiceDescriptor.Singleton(_registrationStub));
                foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
                    services.Remove(descriptor);
                services.PostConfigure<QueueOptions>(options =>
                    options.HmacKeyBase64 = Convert.ToBase64String(new byte[32]));
                services.PostConfigure<SmtpOptions>(options =>
                {
                    options.Username = "test-user";
                    options.Password = "test-password";
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
