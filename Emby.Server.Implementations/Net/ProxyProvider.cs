using System;
using System.Net;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Net
{
    /// <inheritdoc />
    /// <remarks>
    /// Delegates to <see cref="ProxyResolver"/> for the actual file/env probe. The DI
    /// instance also re-applies the proxy to <see cref="System.Net.Http.HttpClient.DefaultProxy"/>
    /// as a safety net in case <see cref="ProxyBootstrap"/> wasn't called early in
    /// <c>Program.Main</c>.
    /// </remarks>
    public sealed class ProxyProvider : IProxyProvider
    {
        private readonly IWebProxy? _proxy;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProxyProvider"/> class.
        /// </summary>
        /// <param name="applicationPaths">Application paths used to locate the proxy sidecar file.</param>
        /// <param name="logger">Logger.</param>
        public ProxyProvider(IApplicationPaths applicationPaths, ILogger<ProxyProvider> logger)
        {
            ArgumentNullException.ThrowIfNull(applicationPaths);
            _proxy = ProxyResolver.Resolve(applicationPaths.ConfigurationDirectoryPath, logger);

            if (_proxy is not null)
            {
                // Belt-and-braces: ensure DefaultProxy is set even when Program.cs bootstrap
                // was skipped (e.g. in tests).
                System.Net.Http.HttpClient.DefaultProxy = _proxy;
                WebRequest.DefaultWebProxy = _proxy;
            }
        }

        /// <inheritdoc />
        public bool IsEnabled => _proxy is not null;

        /// <inheritdoc />
        public IWebProxy? GetWebProxy() => _proxy;
    }
}
