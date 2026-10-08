using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mir3.Web.Localization;
using Mir3.Web.Options;

namespace Mir3.Web.Tests;

public sealed class LocalizationTests : IDisposable
{
    private const string WikiUrl = "https://mir3wiki.swoojeff.online/";
    private const string CultureCookieName = ".AspNetCore.Culture";

    private readonly LocalizationFactory _factory = new();

    [Fact]
    public async Task Accept_language_polish_renders_polish_html_lang()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("Accept-Language", "pl-PL,pl;q=0.9");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<html lang=\"pl\"", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pl")]
    [InlineData("pl;q=0.9,en-US;q=0.8")]
    public async Task Neutral_polish_browser_preference_resolves_to_supported_polish_culture(string header)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("Accept-Language", header);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<html lang=\"pl\"", body, StringComparison.Ordinal);
        Assert.Contains("value=\"pl-PL\" lang=\"pl\" aria-pressed=\"true\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_accept_language_falls_back_to_english_html_lang()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("Accept-Language", "de-DE,de;q=0.9");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<html lang=\"en\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_polish_switch_sets_secure_first_party_cookie_and_persists()
    {
        var (token, antiforgeryCookies) = await GetAntiforgeryContextAsync("/");
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await PostLanguageAsync(client, token, antiforgeryCookies, "pl-PL", "/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.ToString());
        var setCookie = GetSetCookieHeaders(response);
        var cultureCookie = Assert.Single(setCookie, cookie => cookie.StartsWith(CultureCookieName, StringComparison.Ordinal));
        // Set-Cookie values are percent-encoded: c%3Dpl-PL%7Cuic%3Dpl-PL
        Assert.Contains("c%3Dpl-PL", cultureCookie, StringComparison.Ordinal);
        Assert.Contains("uic%3Dpl-PL", cultureCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cultureCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cultureCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", cultureCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cultureCookie, StringComparison.OrdinalIgnoreCase);

        // The explicit cookie overrides a later conflicting Accept-Language header.
        using var followUp = new HttpRequestMessage(HttpMethod.Get, "/");
        followUp.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        followUp.Headers.TryAddWithoutValidation("Cookie", ExtractCookiePair(cultureCookie));
        using var followUpResponse = await client.SendAsync(followUp);
        var followUpBody = await followUpResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, followUpResponse.StatusCode);
        Assert.Contains("<html lang=\"pl\"", followUpBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_english_switch_overrides_polish_accept_language()
    {
        var (token, antiforgeryCookies) = await GetAntiforgeryContextAsync("/");
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await PostLanguageAsync(client, token, antiforgeryCookies, "en-US", "/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cultureCookie = Assert.Single(
            GetSetCookieHeaders(response),
            cookie => cookie.StartsWith(CultureCookieName, StringComparison.Ordinal));
        Assert.Contains("c%3Den-US", cultureCookie, StringComparison.Ordinal);

        using var followUp = new HttpRequestMessage(HttpMethod.Get, "/");
        followUp.Headers.TryAddWithoutValidation("Accept-Language", "pl-PL,pl;q=0.9");
        followUp.Headers.TryAddWithoutValidation("Cookie", ExtractCookiePair(cultureCookie));
        using var followUpResponse = await client.SendAsync(followUp);
        var followUpBody = await followUpResponse.Content.ReadAsStringAsync();

        Assert.Contains("<html lang=\"en\"", followUpBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_language_is_rejected_without_setting_a_cookie()
    {
        var (token, antiforgeryCookies) = await GetAntiforgeryContextAsync("/");
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await PostLanguageAsync(client, token, antiforgeryCookies, "fr-FR", "/");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(CultureCookieName, string.Join(";", GetSetCookieHeaders(response)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pl")]
    [InlineData("en")]
    public async Task Short_language_aliases_are_rejected(string alias)
    {
        var (token, cookieHeader) = await GetAntiforgeryContextAsync("/");
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await PostLanguageAsync(client, token, cookieHeader, alias, "/");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(CultureCookieName, string.Join(";", GetSetCookieHeaders(response)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switch_without_antiforgery_token_is_rejected()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["language"] = "pl-PL",
            ["returnUrl"] = "/",
        });

        using var response = await client.PostAsync("/language", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Switch_with_forged_token_without_issued_cookie_is_rejected()
    {
        var (token, _) = await GetAntiforgeryContextAsync("/");
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["language"] = "pl-PL",
            ["returnUrl"] = "/",
            ["__RequestVerificationToken"] = token,
        });

        using var response = await client.PostAsync("/language", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example.net/capture")]
    [InlineData("//evil.example.net/capture")]
    [InlineData("/\\evil.example.net")]
    public async Task Switch_never_redirects_to_an_external_or_ambiguous_return_url(string returnUrl)
    {
        var (token, antiforgeryCookies) = await GetAntiforgeryContextAsync("/");
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await PostLanguageAsync(client, token, antiforgeryCookies, "pl-PL", returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString();
        Assert.NotNull(location);
        Assert.True(location.StartsWith('/'));
        Assert.False(location.StartsWith("//"));
        Assert.DoesNotContain("://", location, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Navigation_contains_the_exact_wiki_link_and_accessible_language_switch()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"href=\"{WikiUrl}\"", body, StringComparison.Ordinal);
        Assert.Contains("name=\"language\"", body, StringComparison.Ordinal);
        Assert.Contains("value=\"pl-PL\"", body, StringComparison.Ordinal);
        Assert.Contains("value=\"en-US\"", body, StringComparison.Ordinal);
        Assert.Contains("name=\"returnUrl\"", body, StringComparison.Ordinal);
        Assert.Contains("__RequestVerificationToken", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switch_preserves_local_return_path_and_download_routes_still_serve_bytes()
    {
        var (token, antiforgeryCookies) = await GetAntiforgeryContextAsync("/download");
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await PostLanguageAsync(client, token, antiforgeryCookies, "pl-PL", "/download");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/download", response.Headers.Location?.ToString());

        using var followUp = new HttpRequestMessage(HttpMethod.Get, "/download");
        followUp.Headers.TryAddWithoutValidation("Accept-Language", "pl-PL");
        using var followUpResponse = await client.SendAsync(followUp);
        var followUpBody = await followUpResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, followUpResponse.StatusCode);
        Assert.Contains("<html lang=\"pl\"", followUpBody, StringComparison.Ordinal);
        Assert.Contains("download", followUpBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Valid_antiforgery_header_with_non_form_body_is_rejected_not_crashed()
    {
        var (token, cookieHeader) = await GetAntiforgeryContextAsync("/");
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/language")
        {
            Content = new StringContent("{\"language\":\"pl-PL\"}", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation("RequestVerificationToken", token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(CultureCookieName, string.Join(";", GetSetCookieHeaders(response)), StringComparison.Ordinal);
    }

    [Fact]
    public void PortalText_pick_returns_polish_for_polish_ui_culture_and_english_otherwise()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
            CultureInfo.CurrentUICulture = new CultureInfo("pl-PL");
            Assert.Equal("polski", PortalText.Pick("english", "polski"));

            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            Assert.Equal("english", PortalText.Pick("english", "polski"));

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            Assert.Equal("english", PortalText.Pick("english", "polski"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    public void Dispose() => _factory.Dispose();

    private async Task<(string Token, string CookieHeader)> GetAntiforgeryContextAsync(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var match = Regex.Match(body, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Expected an antiforgery token on the page.");
        var cookies = GetSetCookieHeaders(response)
            .Select(ExtractCookiePair)
            .ToArray();
        Assert.NotEmpty(cookies);
        return (match.Groups[1].Value, string.Join("; ", cookies));
    }

    private static async Task<HttpResponseMessage> PostLanguageAsync(
        HttpClient client,
        string token,
        string cookieHeader,
        string language,
        string returnUrl)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/language");
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["language"] = language,
            ["returnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = token,
        });
        return await client.SendAsync(request);
    }

    private static List<string> GetSetCookieHeaders(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToList()
            : [];

    private static string ExtractCookiePair(string setCookieHeader)
    {
        var pair = setCookieHeader.Split(';')[0];
        Assert.NotNull(pair);
        return pair;
    }

    private sealed class LocalizationFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"mir3-localization-tests-{Guid.NewGuid():N}");

        public LocalizationFactory() => Directory.CreateDirectory(_root);

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
                foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
                    services.Remove(descriptor);
                services.PostConfigure<QueueOptions>(options =>
                    options.HmacKeyBase64 = Convert.ToBase64String(new byte[32]));
                services.PostConfigure<Options.SmtpOptions>(options =>
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
