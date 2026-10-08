using System;

namespace Launcher.Core
{
    /// <summary>
    /// Validates and normalizes the public HTTPS patch origin used by the launcher.
    /// Only absolute HTTPS URLs without embedded credentials are accepted.
    /// </summary>
    public sealed class PatchOrigin
    {
        public Uri BaseUri { get; }

        private PatchOrigin(Uri baseUri)
        {
            BaseUri = baseUri;
        }

        public static PatchOrigin Parse(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("Patch host is not configured.", nameof(host));

            if (!Uri.TryCreate(host, UriKind.Absolute, out Uri uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Patch host '{host}' must be an absolute HTTPS URL.", nameof(host));

            if (!string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("Patch host must not embed credentials.", nameof(host));

            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("Patch host must not contain a query string or fragment.", nameof(host));

            if (string.IsNullOrEmpty(uri.Host))
                throw new ArgumentException("Patch host is missing a host name.", nameof(host));

            string baseText = uri.AbsoluteUri;

            if (!baseText.EndsWith("/", StringComparison.Ordinal))
                baseText += "/";

            return new PatchOrigin(new Uri(baseText, UriKind.Absolute));
        }

        public Uri ResolveManifestUri()
        {
            return new Uri(BaseUri, PatchManifest.FileName);
        }

        public Uri ResolveFileUri(string fileName)
        {
            return new Uri(BaseUri, PatchPaths.ToWebFileName(fileName));
        }
    }
}
