using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mir3.Web.Options;
using Mir3.Web.Services;

namespace Mir3.Web.Tests;

public sealed class DownloadRangeTests
{
    [Fact]
    public async Task Missing_installer_shows_friendly_unavailable_homepage_without_exception_details()
    {
        using var factory = new DownloadFactory(createArchive: false);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Launcher installer unavailable", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Metadata_reports_configured_version_exact_length_and_lowercase_sha256()
    {
        using var factory = new DownloadFactory(createArchive: true);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DownloadMetadataService>();
        var metadata = service.GetMetadata();
        var bytes = File.ReadAllBytes(factory.ArchivePath);

        Assert.True(metadata.IsAvailable);
        Assert.Equal("4cc883d-2026.09.22", metadata.Version);
        Assert.Equal(bytes.LongLength, metadata.ByteCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), metadata.Sha256);
        Assert.Equal("Mir3-Zircon-Client-2026-09-22.7z", metadata.FileName);
    }

    [Fact]
    public void Linux_statx_fingerprint_maps_containing_filesystem_device_offsets()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var statxType = typeof(DownloadMetadataService).GetNestedType("LinuxStatx", BindingFlags.NonPublic);

        Assert.NotNull(statxType);
        Assert.Equal((IntPtr)136, Marshal.OffsetOf(statxType!, "DeviceMajor"));
        Assert.Equal((IntPtr)140, Marshal.OffsetOf(statxType!, "DeviceMinor"));
    }

    [Fact]
    public async Task Symlink_archive_is_unavailable_instead_of_serving_target_bytes()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var factory = new DownloadFactory(createArchive: true);
        var targetPath = Path.Combine(Path.GetTempPath(), $"mir3-download-target-{Guid.NewGuid():N}.7z");
        try
        {
            File.WriteAllBytes(targetPath, Enumerable.Repeat((byte)0xa5, 4096).ToArray());
            File.Delete(factory.ArchivePath);
            File.CreateSymbolicLink(factory.ArchivePath, targetPath);

            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/download/client-7z");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("Client download unavailable", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(targetPath);
        }
    }

    [Fact]
    public async Task Directory_archive_is_unavailable_instead_of_being_read()
    {
        using var factory = new DownloadFactory(createArchive: true);
        File.Delete(factory.ArchivePath);
        Directory.CreateDirectory(factory.ArchivePath);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/download/client-7z");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Client download unavailable", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Linux_fifo_archive_is_unavailable_instead_of_being_hashed()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var factory = new DownloadFactory(createArchive: true);
        File.Delete(factory.ArchivePath);
        if (mkfifo(factory.ArchivePath, 0x180) != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "mkfifo failed");
        }

        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DownloadMetadataService>();

        var metadata = service.GetMetadata();

        Assert.False(metadata.IsAvailable);
    }

    [Fact]
    public void Open_download_snapshot_keeps_metadata_and_bytes_on_same_file_identity()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var factory = new DownloadFactory(createArchive: true);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DownloadMetadataService>();

        using var snapshot = service.OpenDownload();
        var replacementPath = Path.Combine(factory.RootPath, "replacement.7z");
        File.WriteAllBytes(replacementPath, Enumerable.Repeat((byte)0x3c, factory.ArchiveBytes.Length).ToArray());
        File.Move(replacementPath, factory.ArchivePath, overwrite: true);

        using var copy = new MemoryStream();
        snapshot.Stream.CopyTo(copy);

        Assert.Equal(factory.ArchiveBytes, copy.ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(factory.ArchiveBytes)).ToLowerInvariant(), snapshot.Metadata.Sha256);
        Assert.Equal(factory.ArchiveBytes.LongLength, snapshot.Metadata.ByteCount);
    }

    [Fact]
    public async Task Download_endpoint_returns_friendly_503_when_archive_disappears_after_metadata_read()
    {
        using var factory = new DownloadFactory(createArchive: true);
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<DownloadMetadataService>();
            Assert.True(service.GetMetadata().IsAvailable);
        }

        File.Delete(factory.ArchivePath);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/download/client-7z");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Client download unavailable", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_download_reports_disappearance_as_an_io_failure_after_metadata_read()
    {
        using var factory = new DownloadFactory(createArchive: true);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DownloadMetadataService>();

        Assert.True(service.GetMetadata().IsAvailable);
        File.Delete(factory.ArchivePath);

        Assert.ThrowsAny<IOException>(() => service.OpenDownload());
    }

    [Fact]
    public void Metadata_hash_cache_invalidates_when_archive_length_changes()
    {
        using var factory = new DownloadFactory(createArchive: true);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DownloadMetadataService>();

        var original = service.GetMetadata();
        File.AppendAllBytes(factory.ArchivePath, [17, 29]);
        var changed = service.GetMetadata();
        var changedBytes = File.ReadAllBytes(factory.ArchivePath);

        Assert.Equal(original.ByteCount + 2, changed.ByteCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(changedBytes)).ToLowerInvariant(), changed.Sha256);
        Assert.NotEqual(original.Sha256, changed.Sha256);
    }

    [Fact]
    public void Metadata_hash_cache_invalidates_when_archive_timestamp_changes_without_length_change()
    {
        using var factory = new DownloadFactory(createArchive: true);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DownloadMetadataService>();

        var original = service.GetMetadata();
        var changedBytes = File.ReadAllBytes(factory.ArchivePath);
        changedBytes[0] ^= 0xff;
        File.WriteAllBytes(factory.ArchivePath, changedBytes);
        File.SetLastWriteTimeUtc(factory.ArchivePath, DateTime.UtcNow.AddMinutes(1));
        var changed = service.GetMetadata();

        Assert.Equal(original.ByteCount, changed.ByteCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(changedBytes)).ToLowerInvariant(), changed.Sha256);
        Assert.NotEqual(original.Sha256, changed.Sha256);
    }

    [Fact]
    public void Metadata_reuses_cached_hash_when_verified_fingerprint_is_unchanged()
    {
        using var factory = new DownloadFactory(createArchive: true);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DownloadMetadataService>();
        var originalTimestamp = DateTime.UtcNow.AddHours(-1);
        originalTimestamp = new DateTime(
            originalTimestamp.Ticks - originalTimestamp.Ticks % TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(factory.ArchivePath, originalTimestamp);

        var original = service.GetMetadata();
        var changedBytes = File.ReadAllBytes(factory.ArchivePath);
        changedBytes[0] ^= 0xff;
        File.WriteAllBytes(factory.ArchivePath, changedBytes);
        File.SetLastWriteTimeUtc(factory.ArchivePath, originalTimestamp);

        var cached = service.GetMetadata();

        Assert.Equal(original.ByteCount, cached.ByteCount);
        Assert.Equal(original.Sha256, cached.Sha256);
    }

    [Fact]
    public void Metadata_cache_does_not_bypass_current_symlink_validation()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var factory = new DownloadFactory(createArchive: true);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DownloadMetadataService>();
        Assert.True(service.GetMetadata().IsAvailable);

        var targetPath = Path.Combine(factory.RootPath, "cache-target.7z");
        File.WriteAllBytes(targetPath, factory.ArchiveBytes);
        File.Delete(factory.ArchivePath);
        File.CreateSymbolicLink(factory.ArchivePath, targetPath);

        Assert.False(service.GetMetadata().IsAvailable);
    }

    [Fact]
    public void Open_download_can_skip_hashing_while_preserving_snapshot_bytes()
    {
        using var factory = new DownloadFactory(createArchive: true);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DownloadMetadataService>();

        using var snapshot = service.OpenDownload(computeHash: false);
        using var copy = new MemoryStream();
        snapshot.Stream.CopyTo(copy);

        Assert.True(snapshot.Metadata.IsAvailable);
        Assert.Equal(factory.ArchiveBytes.LongLength, snapshot.Metadata.ByteCount);
        Assert.Empty(snapshot.Metadata.Sha256);
        Assert.Equal(factory.ArchiveBytes, copy.ToArray());
    }

    [Theory]
    [InlineData("/download/launcher", "Mir3-Zircon-Launcher-Setup.exe", "application/octet-stream")]
    [InlineData("/download/client", "Mir3-Zircon-Client-2026-09-22.zip", "application/zip")]
    [InlineData("/download/client-7z", "Mir3-Zircon-Client-2026-09-22.7z", "application/x-7z-compressed")]
    public async Task Each_explicit_artifact_route_streams_its_own_file_with_range_support(
        string path, string fileName, string contentType)
    {
        using var factory = new DownloadFactory(createArchive: true);
        byte[] expected = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(Path.Combine(factory.RootPath, "Mir3-Zircon-Launcher-Setup.exe"), expected);
        File.WriteAllBytes(Path.Combine(factory.RootPath, "Mir3-Zircon-Client-2026-09-22.zip"), expected);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Range = new RangeHeaderValue(4, 11);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(fileName, response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        if (path == "/download/client-7z")
            Assert.Equal(factory.ArchiveBytes[4..12], await response.Content.ReadAsByteArrayAsync());
        else
            Assert.Equal(expected[4..12], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Downloads_navigation_is_html_and_primary_home_action_is_installer()
    {
        using var factory = new DownloadFactory(createArchive: true);
        File.WriteAllBytes(Path.Combine(factory.RootPath, "Mir3-Zircon-Launcher-Setup.exe"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(factory.RootPath, "Mir3-Zircon-Client-2026-09-22.zip"), [4, 5, 6]);
        using var client = factory.CreateClient();

        string home = await client.GetStringAsync("/");
        using var downloads = await client.GetAsync("/download");
        string page = await downloads.Content.ReadAsStringAsync();

        Assert.Equal("text/html", downloads.Content.Headers.ContentType?.MediaType);
        Assert.Contains("href=\"/download/launcher\"", home);
        Assert.Contains("href=\"/download/launcher\"", page);
        Assert.Contains("href=\"/download/client\"", page);
        Assert.Contains("href=\"/download/client-7z\"", page);
        Assert.Contains("without launcher", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%LOCALAPPDATA%", page);
        Assert.Contains("Uninstall", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unavailable_zip_does_not_hide_available_legacy_archive()
    {
        using var factory = new DownloadFactory(createArchive: true);
        using var client = factory.CreateClient();
        using var missing = await client.GetAsync("/download/client");
        using var legacy = await client.GetAsync("/download/client-7z");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
    }

    [Theory]
    [InlineData("/download/launcher")]
    [InlineData("/download/client")]
    [InlineData("/download/client-7z")]
    public async Task Unavailable_download_endpoints_return_same_friendly_503(string path)
    {
        using var factory = new DownloadFactory(createArchive: false);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Client download unavailable", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Range_request_returns_exact_requested_bytes_and_content_range()
    {
        using var factory = new DownloadFactory(createArchive: true);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/download/client-7z");
        request.Headers.Range = new RangeHeaderValue(0, 1023);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var payload = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 0-1023/4096", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(1024, payload.Length);
        Assert.Equal(factory.ArchiveBytes[..1024], payload);
    }

    [Fact]
    public async Task Full_download_is_an_attachment_and_can_be_consumed_as_a_stream()
    {
        using var factory = new DownloadFactory(createArchive: true);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/download/client-7z", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("Mir3-Zircon-Client-2026-09-22.7z", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(4096, copy.Length);
        Assert.Equal(factory.ArchiveBytes, copy.ToArray());
    }

    [Fact]
    public void Path_traversal_filename_is_rejected_by_startup_options_validation()
    {
        using var factory = new DownloadFactory(createArchive: false, configuredFileName: "../outside.7z");

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("download file name", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Download:ZipFileName", "../outside.zip")]
    [InlineData("Download:LauncherFileName", "nested/installer.exe")]
    [InlineData("Download:ZipFileName", "Mir3-Zircon-Client-2026-09-22.7z")]
    public void Unsafe_or_colliding_artifact_names_fail_on_startup(string key, string value)
    {
        using var factory = new DownloadFactory(createArchive: false, extraSetting: (key, value));
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    private sealed class DownloadFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"mir3-download-tests-{Guid.NewGuid():N}");
        private readonly bool _createArchive;
        private readonly string _configuredFileName;
        private readonly (string Key, string Value)? _extraSetting;

        public DownloadFactory(bool createArchive, string? configuredFileName = null, (string Key, string Value)? extraSetting = null)
        {
            _createArchive = createArchive;
            _configuredFileName = configuredFileName ?? "Mir3-Zircon-Client-2026-09-22.7z";
            _extraSetting = extraSetting;
            Directory.CreateDirectory(_root);
            ArchivePath = Path.Combine(_root, "Mir3-Zircon-Client-2026-09-22.7z");
            ArchiveBytes = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
            if (createArchive) File.WriteAllBytes(ArchivePath, ArchiveBytes);
        }

        public string ArchivePath { get; }
        public string RootPath => _root;
        public byte[] ArchiveBytes { get; }

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
                ["Download:FileName"] = _configuredFileName,
                ["Download:Version"] = "4cc883d-2026.09.22"
            };
            if (_extraSetting is { } extraSetting) settings[extraSetting.Key] = extraSetting.Value;
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
            {
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

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string pathname, uint mode);
}
