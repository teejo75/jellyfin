using System;
using System.IO;
using System.Xml.Serialization;

namespace MediaBrowser.Common.Net
{
    /// <summary>
    /// Loads and saves <see cref="ProxySettings"/> as a sidecar XML file in the
    /// configuration directory.
    /// </summary>
    public static class ProxySettingsStore
    {
        /// <summary>
        /// File name of the proxy settings sidecar.
        /// </summary>
        public const string FileName = "proxy.xml";

        private static readonly XmlSerializer _serializer = new XmlSerializer(typeof(ProxySettings));

        /// <summary>
        /// Builds the absolute path to <c>proxy.xml</c> for a given configuration directory.
        /// </summary>
        /// <param name="configurationDirectory">The Jellyfin configuration directory.</param>
        /// <returns>The absolute path to the proxy settings file.</returns>
        public static string GetFilePath(string configurationDirectory)
            => Path.Combine(configurationDirectory, FileName);

        /// <summary>
        /// Loads proxy settings from the configuration directory, or returns a disabled default
        /// when no file exists.
        /// </summary>
        /// <param name="configurationDirectory">The Jellyfin configuration directory.</param>
        /// <returns>The loaded settings; never null.</returns>
        public static ProxySettings Load(string configurationDirectory)
        {
            var path = GetFilePath(configurationDirectory);
            if (!File.Exists(path))
            {
                return new ProxySettings();
            }

            using var stream = File.OpenRead(path);
            var settings = (ProxySettings?)_serializer.Deserialize(stream);
            return settings ?? new ProxySettings();
        }

        /// <summary>
        /// Saves proxy settings to the configuration directory.
        /// </summary>
        /// <param name="configurationDirectory">The Jellyfin configuration directory.</param>
        /// <param name="settings">The settings to write.</param>
        public static void Save(string configurationDirectory, ProxySettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            Directory.CreateDirectory(configurationDirectory);
            var path = GetFilePath(configurationDirectory);
            using var stream = File.Create(path);
            _serializer.Serialize(stream, settings);
        }
    }
}
