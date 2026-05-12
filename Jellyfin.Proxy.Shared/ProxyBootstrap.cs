using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Common.Net
{
    /// <summary>
    /// Applies a configured proxy globally so that every <see cref="HttpClient"/> in the
    /// process — including those instantiated by third-party libraries (e.g. TMDbLib) that
    /// don't use Jellyfin's <c>IHttpClientFactory</c> — routes outbound traffic through it.
    /// </summary>
    /// <remarks>
    /// Must run before any HttpClient is constructed (i.e. before plugin load and before
    /// the IHttpClientFactory handlers are instantiated). Call from <c>Program.Main</c>
    /// after the configuration directory has been resolved.
    /// </remarks>
    public static class ProxyBootstrap
    {
        /// <summary>
        /// Resolves the proxy from the file or environment and assigns it to
        /// <see cref="HttpClient.DefaultProxy"/> and <see cref="WebRequest.DefaultWebProxy"/>.
        /// </summary>
        /// <param name="configDir">The Jellyfin configuration directory.</param>
        /// <param name="logger">Optional logger.</param>
        /// <returns>The applied proxy, or null if none was configured.</returns>
        public static IWebProxy? Apply(string configDir, ILogger? logger = null)
        {
            var proxy = ProxyResolver.Resolve(configDir, logger);
            if (proxy is null)
            {
                return null;
            }

            HttpClient.DefaultProxy = proxy;
            WebRequest.DefaultWebProxy = proxy;
            logger?.LogInformation(
                "HTTP proxy applied globally (HttpClient.DefaultProxy + WebRequest.DefaultWebProxy)");
            return proxy;
        }
    }
}
