using System;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaBrowser.Common.Net
{
    /// <summary>
    /// Builds an <see cref="IWebProxy"/> from <c>proxy.xml</c> in the configuration directory,
    /// falling back to the standard <c>HTTPS_PROXY</c>/<c>HTTP_PROXY</c>/<c>ALL_PROXY</c>
    /// environment variables.
    /// </summary>
    public static class ProxyResolver
    {
        /// <summary>
        /// Resolves the effective proxy, or null if neither file nor environment configures one.
        /// </summary>
        /// <param name="configDir">Jellyfin configuration directory.</param>
        /// <param name="logger">Optional logger for diagnostic output.</param>
        /// <returns>An <see cref="IWebProxy"/> or null.</returns>
        public static IWebProxy? Resolve(string configDir, ILogger? logger = null)
        {
            logger ??= NullLogger.Instance;
            return BuildFromFile(configDir, logger) ?? BuildFromEnvironment(logger);
        }

        private static IWebProxy? BuildFromFile(string configDir, ILogger logger)
        {
            ProxySettings settings;
            try
            {
                settings = ProxySettingsStore.Load(configDir);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load proxy settings from {Dir}", configDir);
                return null;
            }

            if (!settings.Enabled)
            {
                return null;
            }

            var uri = settings.BuildUri();
            if (uri is null)
            {
                logger.LogWarning("Proxy is enabled in proxy.xml but Address is empty; ignoring");
                return null;
            }

            var proxy = new WebProxy(uri) { BypassProxyOnLocal = false };
            var credentials = BuildCredentials(settings, logger);
            if (credentials is not null)
            {
                proxy.Credentials = credentials;
                logger.LogInformation(
                    "Proxy configured from proxy.xml: {Uri} (user '{User}', credentials attached)",
                    uri,
                    settings.Username);
            }
            else
            {
                logger.LogWarning(
                    "Proxy configured from proxy.xml: {Uri} — credentials NOT attached (username '{User}'). Requests will fail with 407 if the proxy requires auth.",
                    uri,
                    settings.Username ?? "(none)");
            }

            return proxy;
        }

        private static NetworkCredential? BuildCredentials(ProxySettings settings, ILogger logger)
        {
            if (string.IsNullOrEmpty(settings.Username))
            {
                logger.LogDebug("No proxy username configured; credentials not attached");
                return null;
            }

            if (!OperatingSystem.IsWindows())
            {
                logger.LogWarning("Proxy authentication requires Windows DPAPI; running on non-Windows OS — credentials ignored");
                return null;
            }

            if (string.IsNullOrEmpty(settings.EncryptedPassword))
            {
                logger.LogWarning("Proxy username '{User}' is set but EncryptedPassword is empty", settings.Username);
                return new NetworkCredential(settings.Username, string.Empty);
            }

            try
            {
                var password = DecryptOnWindows(settings.EncryptedPassword);
                return new NetworkCredential(settings.Username, password);
            }
            catch (FormatException ex)
            {
                logger.LogError(
                    ex,
                    "EncryptedPassword in proxy.xml is not valid Base64. Did you hand-edit the file? Use jellyfin-proxyconfig.exe to set the password — it must be DPAPI-encrypted.");
                return null;
            }
            catch (CryptographicException ex)
            {
                logger.LogError(
                    ex,
                    "Failed to DPAPI-decrypt proxy password. This usually means the ciphertext was produced on a different machine, by a different user, or hand-written. Use jellyfin-proxyconfig.exe on THIS machine to set it.");
                return null;
            }
        }

        [SupportedOSPlatform("windows")]
        private static string DecryptOnWindows(string? encrypted)
            => ProxyEncryption.Unprotect(encrypted);

        private static IWebProxy? BuildFromEnvironment(ILogger logger)
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
                logger.LogWarning("Environment proxy variable is not a valid URI: {Url}", url);
                return null;
            }

            logger.LogInformation("Using proxy from environment: {Uri}", uri);
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
