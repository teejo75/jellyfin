using System;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Net
{
    /// <inheritdoc />
    /// <remarks>
    /// Resolution order:
    /// 1. <c>proxy.xml</c> in the configuration directory (if Enabled).
    /// 2. <c>HTTPS_PROXY</c> / <c>HTTP_PROXY</c> / <c>ALL_PROXY</c> environment variables.
    /// 3. No proxy.
    /// Settings are read once at construction; restart the server to pick up changes.
    /// </remarks>
    public sealed class ProxyProvider : IProxyProvider
    {
        private readonly ILogger<ProxyProvider> _logger;
        private readonly IWebProxy? _proxy;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProxyProvider"/> class.
        /// </summary>
        /// <param name="applicationPaths">Application paths used to locate the proxy sidecar file.</param>
        /// <param name="logger">Logger.</param>
        public ProxyProvider(IApplicationPaths applicationPaths, ILogger<ProxyProvider> logger)
        {
            ArgumentNullException.ThrowIfNull(applicationPaths);
            _logger = logger;

            _proxy = BuildFromFile(applicationPaths.ConfigurationDirectoryPath)
                  ?? BuildFromEnvironment();

            IsEnabled = _proxy is not null;
            if (IsEnabled)
            {
                _logger.LogInformation("HTTP proxy enabled for outbound connections");
            }
        }

        /// <inheritdoc />
        public bool IsEnabled { get; }

        /// <inheritdoc />
        public IWebProxy? GetWebProxy() => _proxy;

        private IWebProxy? BuildFromFile(string configDir)
        {
            ProxySettings settings;
            try
            {
                settings = ProxySettingsStore.Load(configDir);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load proxy settings from {Dir}", configDir);
                return null;
            }

            if (!settings.Enabled)
            {
                return null;
            }

            var uri = settings.BuildUri();
            if (uri is null)
            {
                _logger.LogWarning("Proxy is enabled but Address is empty; ignoring");
                return null;
            }

            var proxy = new WebProxy(uri) { BypassProxyOnLocal = false };
            if (!string.IsNullOrEmpty(settings.Username))
            {
                proxy.Credentials = BuildCredentials(settings);
            }

            return proxy;
        }

        private NetworkCredential? BuildCredentials(ProxySettings settings)
        {
            if (!OperatingSystem.IsWindows())
            {
                _logger.LogWarning("Proxy authentication requires Windows DPAPI; running on non-Windows OS — credentials ignored");
                return null;
            }

            try
            {
                var password = DecryptOnWindows(settings.EncryptedPassword);
                return new NetworkCredential(settings.Username, password);
            }
            catch (CryptographicException ex)
            {
                _logger.LogError(ex, "Failed to decrypt proxy password — proxy will be used without authentication");
                return null;
            }
        }

        [SupportedOSPlatform("windows")]
        private static string DecryptOnWindows(string? encrypted)
            => ProxyEncryption.Unprotect(encrypted);

        private IWebProxy? BuildFromEnvironment()
        {
            var url = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                   ?? Environment.GetEnvironmentVariable("https_proxy")
                   ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
                   ?? Environment.GetEnvironmentVariable("http_proxy")
                   ?? Environment.GetEnvironmentVariable("ALL_PROXY")
                   ?? Environment.GetEnvironmentVariable("all_proxy");

            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                _logger.LogWarning("Environment proxy variable is not a valid URI: {Url}", url);
                return null;
            }

            _logger.LogInformation("Using proxy from environment: {Uri}", uri);
            var proxy = new WebProxy(uri) { BypassProxyOnLocal = false };
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                proxy.Credentials = new NetworkCredential(
                    Uri.UnescapeDataString(parts[0]),
                    parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
            }

            return proxy;
        }
    }
}
