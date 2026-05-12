using System;
using System.Xml.Serialization;

namespace MediaBrowser.Common.Net
{
    /// <summary>
    /// Persisted HTTP/HTTPS proxy settings.
    /// </summary>
    /// <remarks>
    /// Loaded from <c>proxy.xml</c> in the configuration directory. Password is stored
    /// encrypted via DPAPI (Windows) and never serialized in plaintext.
    /// </remarks>
    [XmlRoot("ProxySettings")]
    public class ProxySettings
    {
        /// <summary>
        /// Gets or sets a value indicating whether the proxy is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the proxy host (without scheme), e.g. "proxy.example.com".
        /// </summary>
        public string? Address { get; set; }

        /// <summary>
        /// Gets or sets the proxy port.
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to use HTTPS to reach the proxy itself.
        /// </summary>
        public bool UseHttps { get; set; }

        /// <summary>
        /// Gets or sets the username for authenticating to the proxy.
        /// </summary>
        public string? Username { get; set; }

        /// <summary>
        /// Gets or sets the DPAPI-encrypted password as a Base64 string.
        /// </summary>
        public string? EncryptedPassword { get; set; }

        /// <summary>
        /// Gets the proxy URI built from <see cref="Address"/>, <see cref="Port"/> and <see cref="UseHttps"/>.
        /// </summary>
        /// <returns>The proxy <see cref="Uri"/>, or null if address is empty.</returns>
        public Uri? BuildUri()
        {
            if (string.IsNullOrWhiteSpace(Address))
            {
                return null;
            }

            var scheme = UseHttps ? "https" : "http";
            var port = Port > 0 ? Port : (UseHttps ? 443 : 8080);
            return new Uri($"{scheme}://{Address}:{port}");
        }
    }
}
