using System.Net;

namespace MediaBrowser.Common.Net
{
    /// <summary>
    /// Resolves the effective HTTP proxy for outbound connections.
    /// </summary>
    public interface IProxyProvider
    {
        /// <summary>
        /// Gets the effective proxy. Returns null when no proxy is configured.
        /// </summary>
        /// <returns>An <see cref="IWebProxy"/> or null.</returns>
        IWebProxy? GetWebProxy();

        /// <summary>
        /// Gets a value indicating whether a proxy is configured and enabled.
        /// </summary>
        bool IsEnabled { get; }
    }
}
