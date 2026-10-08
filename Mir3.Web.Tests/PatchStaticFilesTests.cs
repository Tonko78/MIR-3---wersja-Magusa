using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mir3.Web.Tests;

public sealed class PatchStaticFilesTests
{
    [Fact]
    public async Task Manifest_is_served_from_the_configured_patch_directory()
    {
        var manifestBytes = new byte[] { 1, 2, 3, 4, 5 };
        using var factory = new PatchFactory(files: new Dictionary<string, byte[]>
        {
            ["PList.Bin"] = manifestBytes,
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/patch/PList.Bin");
        var payload = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(manifestBytes, payload);
    }

    [Fact]
    public async Task Compressed_payload_is_served_from_the_configured_patch_directory()
    {
        var payloadBytes = Enumerable.Range(0, 256).Select(index => (byte)index).ToArray();
        using var factory = new PatchFactory(files: new Dictionary<string, byte[]>
        {
            ["Zircon.exe.gz"] = payloadBytes,
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/patch/Zircon.exe.gz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(payloadBytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Missing_patch_file_returns_not_found()
    {
        using var factory = new PatchFactory(files: new Dictionary<string, byte[]>());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/patch/nope.gz");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Encoded_traversal_outside_the_patch_directory_is_not_served()
    {
        using var factory = new PatchFactory(files: new Dictionary<string, byte[]>());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/patch/%2e%2e/%2e%2e/appsettings.json");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Parent_traversal_in_patch_directory_is_rejected_by_startup_options_validation()
    {
        using var factory = new PatchFactory(
            files: new Dictionary<string, byte[]>(),
            configuredDirectory: Path.Combine(Path.GetTempPath(), "..", "escape"),
            createDirectory: false);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("patch directory", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PatchFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"mir3-patch-tests-{Guid.NewGuid():N}");
        private readonly string _configuredDirectory;
        private readonly IReadOnlyDictionary<string, byte[]> _files;

        public PatchFactory(IReadOnlyDictionary<string, byte[]> files, string? configuredDirectory = null, bool createDirectory = true)
        {
            _files = files;
            _configuredDirectory = configuredDirectory ?? Path.Combine(_root, "patch");

            if (createDirectory)
            {
                Directory.CreateDirectory(_configuredDirectory);

                foreach (var pair in _files)
                    File.WriteAllBytes(Path.Combine(_configuredDirectory, pair.Key), pair.Value);
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var queue = Path.Combine(_root, "queue");
            var locks = Path.Combine(_root, "locks");
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
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
                ["Download:Version"] = "4cc883d-2026.09.22",
                ["Patch:Directory"] = _configuredDirectory,
                ["Patch:RequestPath"] = "/patch"
            }));
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)).ToList())
                    services.Remove(descriptor);
                services.PostConfigure<Options.QueueOptions>(options =>
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
