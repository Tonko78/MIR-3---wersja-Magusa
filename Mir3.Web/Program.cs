using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.FileProviders;
using Mir3.Web.Domain;
using Mir3.Web.Data;
using Mir3.Web.Localization;
using Mir3.Web.Options;
using Mir3.Web.Security;
using Mir3.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var portalConnectionString = builder.Configuration.GetConnectionString("Portal")
    ?? throw new InvalidOperationException("Connection string 'Portal' is not configured.");
var verificationLockDirectory = builder.Configuration["VerificationDelivery:LockDirectory"]
    ?? throw new InvalidOperationException("Verification delivery lock directory is not configured.");

builder.Services.Configure<ForwardedHeadersOptions>(options =>
    TrustedProxyConfiguration.Apply(options, builder.Configuration["MIR3_TRUSTED_PROXIES"]));
builder.Services.Configure<HttpsRedirectionOptions>(options => options.HttpsPort = 443);
builder.Services.Configure<HstsOptions>(options => options.ExcludedHosts.Clear());

builder.Services.Configure<CookiePolicyOptions>(options =>
{
    options.HttpOnly = HttpOnlyPolicy.Always;
    options.MinimumSameSitePolicy = SameSiteMode.Strict;
    options.Secure = builder.Environment.IsProduction()
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsProduction()
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("registration", context =>
        HttpMethods.IsPost(context.Request.Method)
            ? FixedWindow(
                RateLimitPartitionKey(context.Connection.RemoteIpAddress),
                5,
                TimeSpan.FromHours(1))
            : RateLimitPartition.GetNoLimiter("registration-read"));
    options.AddPolicy("verification-resend", context =>
        HttpMethods.IsPost(context.Request.Method) &&
        string.Equals(context.Request.Query["handler"], "Resend", StringComparison.OrdinalIgnoreCase)
            ? FixedWindow(
                CompositeRateLimitPartitionKey(
                    context.Connection.RemoteIpAddress,
                    CanonicalRegistrationPartition(context.Request.Query["registration"].ToString())),
                3,
                TimeSpan.FromHours(1))
            : RateLimitPartition.GetNoLimiter("verification-read"));
    options.AddPolicy("admin-login", context =>
        HttpMethods.IsPost(context.Request.Method)
            ? FixedWindow(
                RateLimitPartitionKey(context.Connection.RemoteIpAddress),
                5,
                TimeSpan.FromMinutes(15))
            : RateLimitPartition.GetNoLimiter("admin-login-read"));
    options.AddPolicy("status", context => FixedWindow(
        RateLimitPartitionKey(context.Connection.RemoteIpAddress),
        30,
        TimeSpan.FromMinutes(1)));
});

builder.Services.AddDbContextFactory<PortalDbContext>(options => options.UseSqlite(portalConnectionString));
builder.Services.AddScoped(serviceProvider =>
    serviceProvider.GetRequiredService<IDbContextFactory<PortalDbContext>>().CreateDbContext());
builder.Services.AddSingleton<IConfigureOptions<QueueOptions>, QueueOptionsSetup>();
builder.Services.AddSingleton<IValidateOptions<QueueOptions>, QueueOptionsValidator>();
builder.Services.AddOptions<QueueOptions>().ValidateOnStart();
builder.Services.AddSingleton<IConfigureOptions<SmtpOptions>, SmtpOptionsSetup>();
builder.Services.AddSingleton<IValidateOptions<SmtpOptions>, SmtpOptionsValidator>();
builder.Services.AddOptions<SmtpOptions>().ValidateOnStart();
builder.Services.Configure<DownloadOptions>(builder.Configuration.GetSection(DownloadOptions.SectionName));
builder.Services.AddSingleton<IValidateOptions<DownloadOptions>, DownloadOptionsValidator>();
builder.Services.AddOptions<DownloadOptions>().ValidateOnStart();
builder.Services.Configure<PatchOptions>(builder.Configuration.GetSection(PatchOptions.SectionName));
builder.Services.AddSingleton<IValidateOptions<PatchOptions>, PatchOptionsValidator>();
builder.Services.AddOptions<PatchOptions>().ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher<AdminUser>, AdminPasswordHasher>();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = AdminAuthentication.Scheme;
        options.DefaultChallengeScheme = AdminAuthentication.Scheme;
        options.DefaultSignInScheme = AdminAuthentication.Scheme;
    })
    .AddCookie(AdminAuthentication.Scheme, options =>
    {
        options.Cookie.Name = AdminAuthentication.CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.LoginPath = "/Admin/Login";
        options.AccessDeniedPath = "/Admin/Login";
        options.SlidingExpiration = false;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    });
builder.Services.AddAuthorization(options =>
    options.AddPolicy(AdminAuthentication.Policy, policy =>
    {
        policy.AddAuthenticationSchemes(AdminAuthentication.Scheme);
        policy.RequireAuthenticatedUser();
    }));
builder.Services.AddSingleton(new VerificationTransportLockManager(
    portalConnectionString,
    verificationLockDirectory));
builder.Services.AddSingleton<GamePasswordHasher>();
builder.Services.AddSingleton<VerificationTokenService>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddSingleton<EmailVerificationService>();
builder.Services.AddSingleton<RegistrationStateMachine>();
builder.Services.AddSingleton<AccountQueueClient>();
builder.Services.AddSingleton<RegistrationOutboxProcessor>();
builder.Services.AddSingleton<IRegistrationService, RegistrationService>();
builder.Services.AddSingleton<DownloadMetadataService>();
builder.Services.AddSingleton<DownloadArtifactCatalog>();
builder.Services.AddScoped<AdminPortalService>();
builder.Services.AddHostedService<RegistrationOutboxWorker>();
builder.Services.AddHostedService<AccountQueueResultWorker>();
builder.Services.AddRazorPages();
// Polish copy contains non-ASCII characters; the default encoder would emit
// HTML numeric entities for every diacritic. Allow the full Unicode range so
// responses stay readable, compact UTF-8 (only markup metacharacters encode).
builder.Services.AddSingleton(HtmlEncoder.Create(UnicodeRanges.All));

var supportedCultures = new[] { new CultureInfo("pl-PL"), new CultureInfo("en-US") };
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture("en-US");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
    // Explicit first-party cookie wins over the browser Accept-Language header.
    options.RequestCultureProviders =
    [
        new CookieRequestCultureProvider(),
        // ASP.NET's header provider does not promote neutral "pl" to pl-PL.
        // Honor browser quality weights while resolving neutral/region variants.
        new CustomRequestCultureProvider(context =>
        {
            var culture = context.Request.GetTypedHeaders().AcceptLanguage?
                .Where(entry => entry.Quality.GetValueOrDefault(1) > 0)
                .OrderByDescending(entry => entry.Quality.GetValueOrDefault(1))
                .Take(10)
                .Select(entry => entry.Value.Value)
                .Select(tag => tag is not null &&
                    (tag.Equals("pl", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("pl-", StringComparison.OrdinalIgnoreCase))
                        ? "pl-PL"
                        : tag is not null &&
                          (tag.Equals("en", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
                            ? "en-US"
                            : null)
                .FirstOrDefault(value => value is not null);
            return Task.FromResult<ProviderCultureResult?>(culture is null ? null : new ProviderCultureResult(culture));
        }),
    ];
});

var app = builder.Build();

_ = app.Services.GetRequiredService<IOptions<SmtpOptions>>().Value;
_ = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
await InitializePortalAsync(app.Services, builder.Configuration);

// The launcher patch repository is served as static content from a directory
// managed by ops/mir3-web/publish-patch.ps1 (released via an atomic symlink
// switch on 'current'). The service only needs read access and never creates
// that path itself: if the release has not been published yet the route
// simply stays disabled instead of failing startup.
var patchOptions = app.Services.GetRequiredService<IOptions<PatchOptions>>().Value;
var patchRoot = Path.GetFullPath(patchOptions.Directory);
var patchRequestPath = patchOptions.RequestPath;

if (!Directory.Exists(patchRoot))
{
    Console.WriteLine($"Patch repository disabled: '{patchRoot}' does not exist yet; run ops/mir3-web/publish-patch.ps1.");
}
else
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(patchRoot),
        RequestPath = patchRequestPath,
    });
}

app.UseForwardedHeaders();

if (app.Environment.IsProduction())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}
else if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.Use(async (context, next) =>
{
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next(context);
});
app.UseCookiePolicy();
app.UseRouting();
app.UseRequestLocalization();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapGet("/download/launcher", (DownloadArtifactCatalog catalog) =>
    ServeDownload(catalog.Launcher, "application/octet-stream"));
app.MapGet("/download/client", (DownloadArtifactCatalog catalog) =>
    ServeDownload(catalog.Zip, "application/zip"));
app.MapGet("/download/client-7z", (DownloadArtifactCatalog catalog) =>
    ServeDownload(catalog.Legacy, "application/x-7z-compressed"));
// Manual PL/EN switch: antiforgery-validated POST that persists the choice in a
// first-party culture cookie and redirects only to sanitized local paths. The
// form is read manually (no [FromForm]) so antiforgery failures return 400
// instead of the middleware's exception path.
app.MapPost("/language", async Task<IResult> (
    HttpContext context,
    IAntiforgery antiforgery,
    IWebHostEnvironment environment) =>
{
    if (!context.Request.HasFormContentType)
        return Results.BadRequest();

    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest();
    }

    IFormCollection form;
    try
    {
        form = await context.Request.ReadFormAsync();
    }
    catch (InvalidDataException)
    {
        return Results.BadRequest();
    }
    var language = form["language"].ToString();
    var returnUrl = form["returnUrl"].ToString();

    var culture = language switch
    {
        "pl-PL" => "pl-PL",
        "en-US" => "en-US",
        _ => null,
    };
    if (culture is null)
    {
        return Results.BadRequest();
    }

    context.Response.Cookies.Append(
        CookieRequestCultureProvider.DefaultCookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
        new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = environment.IsProduction(),
            Expires = DateTimeOffset.UtcNow.AddYears(1),
            IsEssential = true,
        });

    return Results.LocalRedirect(IsSafeLocalReturnUrl(returnUrl) ? returnUrl! : "/");
});

app.Run();

static bool IsSafeLocalReturnUrl(string? returnUrl) =>
    !string.IsNullOrEmpty(returnUrl)
    && returnUrl[0] == '/'
    && (returnUrl.Length == 1 || returnUrl[1] is not ('/' or '\\'))
    && !returnUrl.Contains("://", StringComparison.Ordinal)
    && !returnUrl.Contains('\\');

static IResult ServeDownload(DownloadMetadataService downloads, string contentType)
{
    DownloadSnapshot? snapshot = null;
    FileStream? stream = null;
    try
    {
        // Open and serve one verified handle. A file replacement after opening
        // cannot change the file represented by this stream.
        snapshot = downloads.OpenDownload(computeHash: false);
        var metadata = snapshot.Metadata;
        stream = snapshot.TakeStream();
        var result = Results.File(
            stream,
            contentType,
            metadata.FileName,
            enableRangeProcessing: true);
        stream = null;
        return result;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        return UnavailableDownloadResult();
    }
    finally
    {
        stream?.Dispose();
        snapshot?.Dispose();
    }
}

static IResult UnavailableDownloadResult()
{
    // The request localization middleware has already set the current UI culture.
    var isPolish = string.Equals(
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
        "pl",
        StringComparison.Ordinal);
    var lang = isPolish ? "pl" : "en";
    var title = PortalText.Pick("Client download unavailable", "Pobieranie klienta niedostępne");
    var message = PortalText.Pick(
        "The client archive is temporarily unavailable. Please return to the portal and try again later.",
        "Archiwum klienta jest chwilowo niedostępne. Wróć do portalu i spróbuj ponownie później.");
    var back = PortalText.Pick("Return to the portal", "Wróć do portalu");

    return Results.Content(
        $$"""
        <!doctype html>
        <html lang="{{lang}}">
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{{title}} | Mir3 Zircon</title></head>
        <body style="margin:0;background:#0d1110;color:#e5ddc9;font:1rem/1.6 system-ui,sans-serif"><main style="max-width:42rem;margin:12vh auto;padding:2rem"><p style="color:#c7a85b;letter-spacing:.14em;text-transform:uppercase">Mir3 / Zircon</p><h1>{{title}}</h1><p>{{message}}</p><p><a href="/" style="color:#55b08a">{{back}}</a></p></main></body>
        </html>
        """,
        "text/html",
        System.Text.Encoding.UTF8,
        StatusCodes.Status503ServiceUnavailable);
}


static RateLimitPartition<string> FixedWindow(string key, int permitLimit, TimeSpan window) =>
    RateLimitPartition.GetFixedWindowLimiter(
        key,
        _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = permitLimit,
            QueueLimit = 0,
            Window = window
        });

static string RateLimitPartitionKey(IPAddress? address) =>
    HashPartitionValue((address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address)?.ToString() ?? "unknown");

static string CompositeRateLimitPartitionKey(IPAddress? address, string registrationReference) =>
    HashPartitionValue($"{RateLimitPartitionKey(address)}\0{registrationReference}");

static string CanonicalRegistrationPartition(string registrationReference) =>
    Guid.TryParse(registrationReference, out var parsed) && parsed != Guid.Empty
        ? parsed.ToString("N")
        : "invalid";

static string HashPartitionValue(string value) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

static async Task InitializePortalAsync(IServiceProvider services, IConfiguration configuration)
{
    await using var scope = services.CreateAsyncScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PortalDbContext>>();
    await using var database = await factory.CreateDbContextAsync();
    await database.Database.MigrateAsync();

    var bootstrapUsername = configuration["MIR3_ADMIN_USERNAME"];
    var bootstrapPassword = configuration["MIR3_ADMIN_INITIAL_PASSWORD"];
    var hasAdmin = await database.AdminUsers.AnyAsync();
    string? bootstrapPasswordHash = null;
    if (!hasAdmin && !string.IsNullOrWhiteSpace(bootstrapUsername) && !string.IsNullOrWhiteSpace(bootstrapPassword))
    {
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AdminUser>>();
        bootstrapPasswordHash = hasher.HashPassword(new AdminUser(), bootstrapPassword);
    }

    await PortalDbInitializer.InitializeAsync(
        database,
        bootstrapUsername,
        bootstrapPasswordHash);

    if (!await database.AdminUsers.AnyAsync())
    {
        throw new InvalidOperationException(
            "No administrator account exists. Set MIR3_ADMIN_USERNAME and MIR3_ADMIN_INITIAL_PASSWORD for first startup.");
    }
}

public partial class Program;
