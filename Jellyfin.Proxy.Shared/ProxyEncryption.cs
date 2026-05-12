using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace MediaBrowser.Common.Net
{
    /// <summary>
    /// DPAPI helpers for encrypting/decrypting the proxy password at rest.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="DataProtectionScope.LocalMachine"/> so the encrypted blob
    /// can be read regardless of which Windows account runs the Jellyfin service.
    /// Anyone with administrative access to the machine can decrypt.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static class ProxyEncryption
    {
        private static readonly byte[] _entropy = Encoding.UTF8.GetBytes("Jellyfin.ProxySettings.v1");

        /// <summary>
        /// Encrypts a plaintext password and returns a Base64-encoded ciphertext.
        /// </summary>
        /// <param name="plaintext">The plaintext to protect.</param>
        /// <returns>Base64 ciphertext, or empty string if input is null/empty.</returns>
        public static string Protect(string? plaintext)
        {
            if (string.IsNullOrEmpty(plaintext))
            {
                return string.Empty;
            }

            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var cipher = ProtectedData.Protect(bytes, _entropy, DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(cipher);
        }

        /// <summary>
        /// Decrypts a Base64-encoded ciphertext back to plaintext.
        /// </summary>
        /// <param name="encryptedBase64">Base64 ciphertext produced by <see cref="Protect"/>.</param>
        /// <returns>Plaintext, or empty string if input is null/empty.</returns>
        /// <exception cref="CryptographicException">Decryption failed (e.g. blob originated on a different machine).</exception>
        public static string Unprotect(string? encryptedBase64)
        {
            if (string.IsNullOrEmpty(encryptedBase64))
            {
                return string.Empty;
            }

            var cipher = Convert.FromBase64String(encryptedBase64);
            var plain = ProtectedData.Unprotect(cipher, _entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
    }
}
