using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mir3.Web.Data;
using Mir3.Web.Domain;
using Mir3.Web.Options;
using Mir3.Web.Services;

namespace Mir3.Web.Tests;

public sealed class AdminAuthorizationTests
{
    private const string AdminUsername = "portal-admin";
    private const string AdminPassword = "InitialAdminPass7!";

    [Fact]
    public async Task Polish_admin_login_dashboard_settings_and_action_message()
    {
        using var factory = new AdminPortalFactory("Development");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pl-PL");

        var loginPage = await GetLoginPageAsync(client);
        Assert.Contains("<html lang=\"pl\"", loginPage.Body, StringComparison.Ordinal);
        Assert.Contains("<h1>Logowanie administratora</h1>", loginPage.Body, StringComparison.Ordinal);
        using var badLogin = await PostLoginAsync(client, loginPage.AntiForgeryToken, AdminUsername, "wrong-password");
        Assert.Contains("Nieprawidłowa nazwa użytkownika lub hasło.", await badLogin.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var cookie = await LoginAndGetAdminCookieAsync(client);
        using var dashboard = await GetAdminPageAsync(client, cookie, "/Admin");
        var dashboardHtml = await dashboard.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Panel administratora</h1>", dashboardHtml, StringComparison.Ordinal);
        Assert.Contains("Rejestracje", dashboardHtml, StringComparison.Ordinal);
        using var settings = await GetAdminPageAsync(client, cookie, "/Admin/Settings");
        var settingsHtml = await settings.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Ustawienia administratora</h1>", settingsHtml, StringComparison.Ordinal);

        using var action = await PostAdminFormAsync(client, cookie, "/Admin?handler=Activate", new Dictionary<string, string>
        {
            ["RegistrationId"] = Guid.Empty.ToString("D"),
            ["ConfirmText"] = "CONFIRM",
            ["__RequestVerificationToken"] = ExtractAntiForgeryToken(dashboardHtml)
        });
        Assert.Equal(HttpStatusCode.OK, action.StatusCode);
        var actionHtml = await action.Content.ReadAsStringAsync();
        Assert.Contains("Nie znaleziono rejestracji.", actionHtml, StringComparison.Ordinal);
        var returnUrl = Regex.Match(actionHtml, "name=\"returnUrl\"[^>]*value=\"([^\"]+)\"");
        Assert.True(returnUrl.Success);
        Assert.Equal("/Admin", WebUtility.HtmlDecode(returnUrl.Groups[1].Value));

        using var saved = await PostAdminFormAsync(client, cookie, "/Admin/Settings", new Dictionary<string, string>
        {
            ["AutoActivate"] = "true",
            ["ConfirmText"] = "CONFIRM",
            ["__RequestVerificationToken"] = ExtractAntiForgeryToken(settingsHtml)
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var auditPage = await GetAdminPageAsync(client, cookie, "/Admin");
        var auditHtml = await auditPage.Content.ReadAsStringAsync();
        Assert.Contains("Zmiana automatycznej aktywacji", auditHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(">Admin.Settings.AutoActivate<", auditHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_utility_classes_are_defined_in_scoped_stylesheet()
    {
        using var factory = new AdminPortalFactory("Development");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/css/site.css");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var css = await response.Content.ReadAsStringAsync();

        Assert.Contains(".admin-shell .text-muted { color: var(--muted); }", css, StringComparison.Ordinal);
        Assert.Contains(".admin-shell .align-middle { vertical-align: middle; }", css, StringComparison.Ordinal);
        Assert.Contains(".admin-shell .table-sm th, .admin-shell .table-sm td { padding: .4rem .5rem; }", css, StringComparison.Ordinal);
        Assert.Contains(".admin-shell .btn-outline-secondary { border-color: var(--muted); color: var(--paper); }", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anonymous_admin_dashboard_redirects_to_admin_login()
    {
        using var factory = new AdminPortalFactory("Development");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/Admin");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Admin/Login", response.Headers.Location?.OriginalString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Valid_admin_credentials_issue_http_only_secure_same_site_strict_cookie()
    {
        using var factory = new AdminPortalFactory("Production", trustedProxy: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var loginPage = await GetLoginPageAsync(client, forwardedProto: "https");
        using var response = await PostLoginAsync(
            client,
            loginPage.AntiForgeryToken,
            AdminUsername,
            AdminPassword,
            ip: "198.51.100.10",
            forwardedProto: "https");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        var adminCookie = Assert.Single(cookies, value =>
            value.StartsWith(".mir3.admin=", StringComparison.Ordinal));
        Assert.Contains("httponly", adminCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", adminCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", adminCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Five_failed_logins_lock_admin_login_for_fifteen_minutes()
    {
        using var factory = new AdminPortalFactory("Development", trustedProxy: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var loginPage = await GetLoginPageAsync(client, ip: $"198.51.100.{attempt}");
            using var response = await PostLoginAsync(
                client,
                loginPage.AntiForgeryToken,
                AdminUsername,
                "wrong-password",
                ip: $"198.51.100.{attempt}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var lockedPage = await GetLoginPageAsync(client, ip: "198.51.100.99");
        using var lockedResponse = await PostLoginAsync(
            client,
            lockedPage.AntiForgeryToken,
            AdminUsername,
            AdminPassword,
            ip: "198.51.100.99");

        Assert.Equal(HttpStatusCode.OK, lockedResponse.StatusCode);
        Assert.DoesNotContain(lockedResponse.Headers, header => header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase));
        var lockedBody = await lockedResponse.Content.ReadAsStringAsync();
        Assert.Contains("incorrect", lockedBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AdminPassword, lockedBody, StringComparison.Ordinal);

        await using var database = await factory.CreatePortalDbContextAsync();
        var admin = Assert.Single(await database.AdminUsers.AsNoTracking().ToListAsync());
        Assert.Equal(5, admin.FailedAttempts);
        var lockout = Assert.IsType<DateTime>(admin.LockoutUntilUtc);
        Assert.Equal(TimeSpan.FromMinutes(15), lockout - admin.UpdatedUtc);
        Assert.True(lockout >= DateTime.UtcNow.AddMinutes(14), $"Lockout until {lockout:O} should still be active for at least 14 minutes.");
    }

    [Fact]
    public async Task Admin_login_gets_do_not_consume_post_rate_limit()
    {
        using var factory = new AdminPortalFactory("Development", trustedProxy: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        for (var attempt = 0; attempt < 5; attempt++)
        {
            _ = await GetLoginPageAsync(client, ip: "198.51.100.200");
        }

        var loginPage = await GetLoginPageAsync(client, ip: "198.51.100.200");
        using var response = await PostLoginAsync(
            client,
            loginPage.AntiForgeryToken,
            AdminUsername,
            "wrong-password",
            ip: "198.51.100.200");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Concurrent_failed_logins_increment_counter_without_lost_updates()
    {
        using var factory = new AdminPortalFactory("Development", trustedProxy: true);
        var attempts = Enumerable.Range(1, 8).Select(async attempt =>
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var loginPage = await GetLoginPageAsync(client, ip: $"198.51.100.{attempt}");
            using var response = await PostLoginAsync(
                client,
                loginPage.AntiForgeryToken,
                AdminUsername,
                "wrong-password",
                ip: $"198.51.100.{attempt}");
            return response.StatusCode;
        });

        var statuses = await Task.WhenAll(attempts);

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));
        await using var database = await factory.CreatePortalDbContextAsync();
        var admin = Assert.Single(await database.AdminUsers.AsNoTracking().ToListAsync());
        Assert.Equal(8, admin.FailedAttempts);
        Assert.NotNull(admin.LockoutUntilUtc);
    }

    [Fact]
    public async Task Successful_login_resets_failure_counters()
    {
        using var factory = new AdminPortalFactory("Development", trustedProxy: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var failurePage = await GetLoginPageAsync(client, ip: $"198.51.100.{attempt}");
            using var failure = await PostLoginAsync(
                client,
                failurePage.AntiForgeryToken,
                AdminUsername,
                "wrong-password",
                ip: $"198.51.100.{attempt}");
            Assert.Equal(HttpStatusCode.OK, failure.StatusCode);
        }

        var successPage = await GetLoginPageAsync(client, ip: "198.51.100.50");
        using var success = await PostLoginAsync(
            client,
            successPage.AntiForgeryToken,
            AdminUsername,
            AdminPassword,
            ip: "198.51.100.50");

        Assert.Equal(HttpStatusCode.Redirect, success.StatusCode);
        await using var database = await factory.CreatePortalDbContextAsync();
        var admin = Assert.Single(await database.AdminUsers.AsNoTracking().ToListAsync());
        Assert.Equal(0, admin.FailedAttempts);
        Assert.Null(admin.LockoutUntilUtc);
    }

    [Fact]
    public async Task Activation_and_settings_posts_without_antiforgery_tokens_return_400()
    {
        using var factory = new AdminPortalFactory("Development", trustedProxy: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var adminCookie = await LoginAndGetAdminCookieAsync(client);

        using var activationResponse = await PostAdminFormAsync(
            client,
            adminCookie,
            "/Admin/Index?handler=Activate",
            new Dictionary<string, string>
            {
                ["RegistrationId"] = Guid.NewGuid().ToString("D"),
                ["ConfirmText"] = "CONFIRM"
            });
        Assert.Equal(HttpStatusCode.BadRequest, activationResponse.StatusCode);

        using var settingsResponse = await PostAdminFormAsync(
            client,
            adminCookie,
            "/Admin/Settings",
            new Dictionary<string, string>
            {
                ["AutoActivate"] = "true",
                ["ConfirmText"] = "CONFIRM"
            });
        Assert.Equal(HttpStatusCode.BadRequest, settingsResponse.StatusCode);
    }

    [Fact]
    public async Task Unverified_account_cannot_be_activated()
    {
        using var factory = new AdminPortalFactory("Development", trustedProxy: true);
        var registration = await factory.SeedRegistrationAsync("pending@example.test", RegistrationStatus.PendingEmail);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var adminCookie = await LoginAndGetAdminCookieAsync(client);

        using var response = await PostConfirmedAdminActionAsync(
            client,
            adminCookie,
            "/Admin/Index",
            "Activate",
            registration.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("cannot be activated", body, StringComparison.OrdinalIgnoreCase);
        await using var database = await factory.CreatePortalDbContextAsync();
        var stored = await database.Registrations.AsNoTracking().SingleAsync(item => item.Id == registration.Id);
        Assert.Equal(RegistrationStatus.PendingEmail, stored.Status);
        Assert.Null(stored.QueueRequestId);
        Assert.Null(stored.QueueCommandType);
    }

    [Fact]
    public async Task Manual_activation_of_verified_account_queues_create_account()
    {
        using var factory = new AdminPortalFactory("Development", trustedProxy: true);
        var registration = await factory.SeedRegistrationAsync(
            "awaiting@example.test",
            RegistrationStatus.AwaitingAdmin,
            verified: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var adminCookie = await LoginAndGetAdminCookieAsync(client);

        using var response = await PostConfirmedAdminActionAsync(
            client,
            adminCookie,
            "/Admin/Index",
            "Activate",
            registration.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("queued", body, StringComparison.OrdinalIgnoreCase);

        await using var database = await factory.CreatePortalDbContextAsync();
        var stored = await database.Registrations.AsNoTracking().SingleAsync(item => item.Id == registration.Id);
        Assert.Equal(RegistrationStatus.QueuePending, stored.Status);
        Assert.Equal(AccountPortal.Contracts.AccountCommandType.CreateAccount, stored.QueueCommandType);
        Assert.NotNull(stored.QueueRequestId);
        Assert.True(
            await WaitUntilAsync(
                () => File.Exists(Path.Combine(factory.QueueIncomingPath, $"{stored.QueueRequestId:D}.json")),
                TimeSpan.FromSeconds(5)),
            "The activation command was not published to the account queue.");
    }

    [Fact]
    public async Task Deactivation_of_active_account_queues_deactivate_account()
    {
        using var factory = new AdminPortalFactory("Development", trustedProxy: true);
        var registration = await factory.SeedRegistrationAsync(
            "active@example.test",
            RegistrationStatus.Active,
            verified: true,
            gameActivated: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var adminCookie = await LoginAndGetAdminCookieAsync(client);

        using var response = await PostConfirmedAdminActionAsync(
            client,
            adminCookie,
            "/Admin/Index",
            "Deactivate",
            registration.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var database = await factory.CreatePortalDbContextAsync();
        var stored = await database.Registrations.AsNoTracking().SingleAsync(item => item.Id == registration.Id);
        Assert.Equal(RegistrationStatus.QueuePending, stored.Status);
        Assert.Equal(AccountPortal.Contracts.AccountCommandType.DeactivateAccount, stored.QueueCommandType);
        Assert.NotNull(stored.QueueRequestId);
        Assert.True(
            await WaitUntilAsync(
                () => File.Exists(Path.Combine(factory.QueueIncomingPath, $"{stored.QueueRequestId:D}.json")),
                TimeSpan.FromSeconds(5)),
            "The deactivation command was not published to the account queue.");
    }

    [Fact]
    public async Task Settings_toggle_writes_audit_entry_with_old_and_new_values()
    {
        using var factory = new AdminPortalFactory("Development", trustedProxy: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var adminCookie = await LoginAndGetAdminCookieAsync(client);

        using var settingsPage = await GetAdminPageAsync(client, adminCookie, "/Admin/Settings");
        var token = ExtractAntiForgeryToken(await settingsPage.Content.ReadAsStringAsync());
        using var response = await PostAdminFormAsync(
            client,
            adminCookie,
            "/Admin/Settings",
            new Dictionary<string, string>
            {
                ["AutoActivate"] = "true",
                ["ConfirmText"] = "CONFIRM",
                ["__RequestVerificationToken"] = token
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var database = await factory.CreatePortalDbContextAsync();
        var setting = await database.PortalSettings.AsNoTracking().SingleAsync(item => item.Id == PortalSetting.SingletonId);
        Assert.True(setting.AutoActivateAfterEmailVerification);
        var audit = await database.AuditEntries.AsNoTracking().OrderByDescending(item => item.Id).ToListAsync();
        var entry = Assert.Single(audit, item => item.Action == "Admin.Settings.AutoActivate");
        Assert.Equal(AdminUsername, entry.Actor);
        Assert.Equal(PortalSetting.SingletonKey, entry.Target);
        Assert.Contains("\"old\":false", entry.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"new\":true", entry.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_settings_model_state_does_not_update_settings()
    {
        using var factory = new AdminPortalFactory("Development", trustedProxy: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var adminCookie = await LoginAndGetAdminCookieAsync(client);

        using var settingsPage = await GetAdminPageAsync(client, adminCookie, "/Admin/Settings");
        var token = ExtractAntiForgeryToken(await settingsPage.Content.ReadAsStringAsync());
        using var enableResponse = await PostAdminFormAsync(
            client,
            adminCookie,
            "/Admin/Settings",
            new Dictionary<string, string>
            {
                ["AutoActivate"] = "true",
                ["ConfirmText"] = "CONFIRM",
                ["__RequestVerificationToken"] = token
            });
        Assert.Equal(HttpStatusCode.OK, enableResponse.StatusCode);

        using var invalidPage = await GetAdminPageAsync(client, adminCookie, "/Admin/Settings");
        var invalidToken = ExtractAntiForgeryToken(await invalidPage.Content.ReadAsStringAsync());
        using var invalidResponse = await PostAdminFormAsync(
            client,
            adminCookie,
            "/Admin/Settings",
            new Dictionary<string, string>
            {
                ["AutoActivate"] = "not-a-boolean",
                ["ConfirmText"] = "CONFIRM",
                ["__RequestVerificationToken"] = invalidToken
            });

        Assert.Equal(HttpStatusCode.OK, invalidResponse.StatusCode);
        await using var database = await factory.CreatePortalDbContextAsync();
        var setting = await database.PortalSettings.AsNoTracking().SingleAsync(item => item.Id == PortalSetting.SingletonId);
        Assert.True(setting.AutoActivateAfterEmailVerification);
    }

    [Fact]
    public async Task Admin_pages_never_expose_password_hashes_or_smtp_errors()
    {
        using var factory = new AdminPortalFactory("Development", trustedProxy: true);
        var registration = await factory.SeedRegistrationAsync(
            "delivery-failure@example.test",
            RegistrationStatus.PendingEmail,
            emailLastErrorCode: "smtp-send");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var adminCookie = await LoginAndGetAdminCookieAsync(client);

        using var dashboard = await GetAdminPageAsync(client, adminCookie, "/Admin/Index");
        var body = await dashboard.Content.ReadAsStringAsync();

        Assert.DoesNotContain("smtp-", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString(registration.PasswordHash), body, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(registration.SourceIpHash), body, StringComparison.Ordinal);

        var failedLoginPage = await GetLoginPageAsync(client, ip: "198.51.100.77");
        using var failedLogin = await PostLoginAsync(
            client,
            failedLoginPage.AntiForgeryToken,
            AdminUsername,
            "wrong-password",
            ip: "198.51.100.77");
        var failedLoginBody = await failedLogin.Content.ReadAsStringAsync();
        Assert.DoesNotContain("PasswordHasher", failedLoginBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PBKDF2", failedLoginBody, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> LoginAndGetAdminCookieAsync(HttpClient client)
    {
        var loginPage = await GetLoginPageAsync(client);
        using var response = await PostLoginAsync(
            client,
            loginPage.AntiForgeryToken,
            AdminUsername,
            AdminPassword);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var adminCookie = response.Headers
            .GetValues("Set-Cookie")
            .SingleOrDefault(value => value.StartsWith(".mir3.admin=", StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(adminCookie), "The admin login did not issue the admin cookie.");
        var separator = adminCookie.IndexOf(';');
        return separator > 0 ? adminCookie[..separator] : adminCookie;
    }

    private static async Task<(string AntiForgeryToken, string Body)> GetLoginPageAsync(
        HttpClient client,
        string? ip = null,
        string? forwardedProto = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Admin/Login");
        AddForwardingHeaders(request, ip, forwardedProto);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var antiforgeryCookie = response.Headers
            .TryGetValues("Set-Cookie", out var setCookies)
            ? setCookies
            .FirstOrDefault(value => value.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal))?
            .Split(';', 2)[0]
            : null;
        if (antiforgeryCookie is not null)
        {
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", antiforgeryCookie);
        }
        return (ExtractAntiForgeryToken(body), body);
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(
        HttpClient client,
        string antiForgeryToken,
        string username,
        string password,
        string? ip = null,
        string? forwardedProto = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/Login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Username"] = username,
                ["Input.Password"] = password,
                ["__RequestVerificationToken"] = antiForgeryToken
            })
        };
        AddForwardingHeaders(request, ip, forwardedProto);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAdminPageAsync(
        HttpClient client,
        string adminCookie,
        string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        AddAdminCookie(request, client, adminCookie);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response;
    }

    private static async Task<HttpResponseMessage> PostConfirmedAdminActionAsync(
        HttpClient client,
        string adminCookie,
        string page,
        string handler,
        Guid registrationId)
    {
        using var pageResponse = await GetAdminPageAsync(client, adminCookie, page);
        var token = ExtractAntiForgeryToken(await pageResponse.Content.ReadAsStringAsync());
        return await PostAdminFormAsync(
            client,
            adminCookie,
            $"{page}?handler={handler}",
            new Dictionary<string, string>
            {
                ["RegistrationId"] = registrationId.ToString("D"),
                ["ConfirmText"] = "CONFIRM",
                ["__RequestVerificationToken"] = token
            });
    }

    private static async Task<HttpResponseMessage> PostAdminFormAsync(
        HttpClient client,
        string adminCookie,
        string path,
        IReadOnlyDictionary<string, string> fields)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        AddAdminCookie(request, client, adminCookie);
        return await client.SendAsync(request);
    }

    private static void AddAdminCookie(HttpRequestMessage request, HttpClient client, string adminCookie)
    {
        var existingCookies = client.DefaultRequestHeaders.TryGetValues("Cookie", out var values)
            ? string.Join("; ", values)
            : string.Empty;
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            string.IsNullOrEmpty(existingCookies) ? adminCookie : $"{adminCookie}; {existingCookies}");
    }

    private static void AddForwardingHeaders(HttpRequestMessage request, string? ip, string? forwardedProto)
    {
        if (ip is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
        }

        if (forwardedProto is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", forwardedProto);
        }
    }

    private static string ExtractAntiForgeryToken(string body)
    {
        var match = Regex.Match(
            body,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, "The page did not contain an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }

        return condition();
    }

    private sealed class AdminPortalFactory(string environment, bool trustedProxy = false) : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"mir3-admin-web-{Guid.NewGuid():N}");

        public string QueueIncomingPath => Path.Combine(_root, "queue", "incoming");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_root);
            var queue = Path.Combine(_root, "queue");
            var locks = Path.Combine(_root, "locks");
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:Portal", $"Data Source={Path.Combine(_root, "portal.db")}");
            builder.UseSetting("VerificationDelivery:LockDirectory", locks);
            builder.UseSetting("Queue:RootPath", queue);
            builder.UseSetting("Queue:ResultPollSeconds", "2");
            builder.UseSetting("MIR3_ADMIN_USERNAME", AdminUsername);
            builder.UseSetting("MIR3_ADMIN_INITIAL_PASSWORD", AdminPassword);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
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
                ["MIR3_ADMIN_USERNAME"] = AdminUsername,
                ["MIR3_ADMIN_INITIAL_PASSWORD"] = AdminPassword
            });

            });
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(
                    new AdminRemoteIpStartupFilter(
                        trustedProxy ? IPAddress.Parse("192.168.0.64") : IPAddress.Loopback));
                var hosted = services.Where(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)).ToList();
                foreach (var descriptor in hosted) services.Remove(descriptor);
                services.PostConfigure<QueueOptions>(options =>
                    options.HmacKeyBase64 = Convert.ToBase64String(new byte[32]));
                services.PostConfigure<SmtpOptions>(options =>
                {
                    options.Host = "smtp.example.invalid";
                    options.Port = 587;
                    options.UseStartTls = true;
                    options.FromAddress = "mir3@example.invalid";
                    options.FromName = "Mir3";
                    options.TimeoutSeconds = 20;
                    options.Username = "test-user";
                    options.Password = "test-password";
                });
            });
        }

        public async Task<PortalDbContext> CreatePortalDbContextAsync()
        {
            var scope = Services.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PortalDbContext>>();
            var context = await factory.CreateDbContextAsync();
            await scope.DisposeAsync();
            return context;
        }

        public async Task<Registration> SeedRegistrationAsync(
            string email,
            RegistrationStatus status,
            bool verified = false,
            bool gameActivated = false,
            string? emailLastErrorCode = null)
        {
            var normalized = email.Trim().ToLowerInvariant();
            var registration = new Registration
            {
                Id = Guid.NewGuid(),
                Email = normalized,
                NormalizedEmail = normalized,
                PasswordHash = RandomNumberGeneratorGetBytes(36),
                Status = status,
                VerificationTokenHash = RandomNumberGeneratorGetBytes(32),
                VerificationExpiresUtc = DateTime.UtcNow.AddHours(1),
                EmailVerifiedUtc = verified ? DateTime.UtcNow.AddMinutes(-5) : null,
                VerificationUsedUtc = verified ? DateTime.UtcNow.AddMinutes(-5) : null,
                GameActivatedUtc = gameActivated ? DateTime.UtcNow.AddMinutes(-3) : null,
                EmailLastErrorCode = emailLastErrorCode,
                InitialEmailDeliveryPending = false,
                CreatedUtc = DateTime.UtcNow.AddHours(-2),
                UpdatedUtc = DateTime.UtcNow.AddMinutes(-1),
                SourceIpHash = RandomNumberGeneratorGetBytes(32)
            };

            await using var database = await CreatePortalDbContextAsync();

            database.Registrations.Add(registration);
            await database.SaveChangesAsync();
            return registration;
        }

        private static byte[] RandomNumberGeneratorGetBytes(int length) =>
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class AdminRemoteIpStartupFilter(IPAddress address) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
            Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(nextMiddleware => context =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
