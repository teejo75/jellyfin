using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaBrowser.Common.Net
{
    /// <summary>
    /// Cross-platform symmetric encryption helper for the proxy password at rest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses AES-256-GCM with a key derived from a machine-stable identifier:
    /// </para>
    /// <list type="bullet">
    ///   <item>Windows: <c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c>.</item>
    ///   <item>Linux: <c>/etc/machine-id</c> (falls back to <c>/var/lib/dbus/machine-id</c>).</item>
    ///   <item>macOS: <c>IOPlatformUUID</c> via <c>ioreg</c>.</item>
    ///   <item>Fallback: hostname + OS platform string.</item>
    /// </list>
    /// <para>
    /// Blob layout: <c>[12-byte nonce][16-byte tag][N-byte ciphertext]</c>, Base64-encoded.
    /// Encrypted blobs are bound to the machine they were produced on — transferring
    /// <c>proxy.xml</c> between hosts will fail decryption.
    /// </para>
    /// <para>
    /// Threat model: anyone who can read the machine identifier on the host can recover
    /// the password. Equivalent to the previous DPAPI <c>LocalMachine</c> scope, but
    /// portable across Windows, Linux and macOS.
    /// </para>
    /// </remarks>
    public static class ProxyEncryption
    {
        private const int NonceSize = 12;
        private const int TagSize = 16;
        private const int KeySize = 32; // AES-256
        private const int Pbkdf2Iterations = 100_000;

        private static readonly byte[] Salt = Encoding.UTF8.GetBytes("Jellyfin.Proxy.v2.salt");

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

            var key = DeriveMachineKey();
            try
            {
                var nonce = RandomNumberGenerator.GetBytes(NonceSize);
                var plain = Encoding.UTF8.GetBytes(plaintext);
                var cipher = new byte[plain.Length];
                var tag = new byte[TagSize];

                using (var aes = new AesGcm(key, TagSize))
                {
                    aes.Encrypt(nonce, plain, cipher, tag);
                }

                var blob = new byte[NonceSize + TagSize + cipher.Length];
                nonce.CopyTo(blob, 0);
                tag.CopyTo(blob, NonceSize);
                cipher.CopyTo(blob, NonceSize + TagSize);
                return Convert.ToBase64String(blob);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        /// <summary>
        /// Decrypts a Base64-encoded ciphertext back to plaintext.
        /// </summary>
        /// <param name="encryptedBase64">Base64 ciphertext produced by <see cref="Protect"/>.</param>
        /// <returns>Plaintext, or empty string if input is null/empty.</returns>
        /// <exception cref="CryptographicException">
        /// Decryption failed. Common cause: the blob was produced on a different machine.
        /// </exception>
        public static string Unprotect(string? encryptedBase64)
        {
            if (string.IsNullOrEmpty(encryptedBase64))
            {
                return string.Empty;
            }

            var blob = Convert.FromBase64String(encryptedBase64);
            if (blob.Length < NonceSize + TagSize)
            {
                throw new CryptographicException("Invalid ciphertext: too short.");
            }

            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            var cipher = new byte[blob.Length - NonceSize - TagSize];
            Array.Copy(blob, 0, nonce, 0, NonceSize);
            Array.Copy(blob, NonceSize, tag, 0, TagSize);
            Array.Copy(blob, NonceSize + TagSize, cipher, 0, cipher.Length);

            var key = DeriveMachineKey();
            var plain = new byte[cipher.Length];
            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(nonce, cipher, tag, plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            return Encoding.UTF8.GetString(plain);
        }

        private static byte[] DeriveMachineKey()
        {
            var machineId = GetMachineId();
            return Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(machineId),
                Salt,
                Pbkdf2Iterations,
                HashAlgorithmName.SHA256,
                KeySize);
        }

        private static string GetMachineId()
        {
            string? id = null;
            if (OperatingSystem.IsWindows())
            {
                id = GetWindowsMachineGuid();
            }
            else if (OperatingSystem.IsLinux())
            {
                id = GetLinuxMachineId();
            }
            else if (OperatingSystem.IsMacOS())
            {
                id = GetMacMachineId();
            }

            return !string.IsNullOrWhiteSpace(id)
                ? id
                : $"{Environment.MachineName}|{Environment.OSVersion.Platform}";
        }

        [SupportedOSPlatform("windows")]
        private static string? GetWindowsMachineGuid()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Cryptography",
                    writable: false);
                return key?.GetValue("MachineGuid") as string;
            }
            catch
            {
                return null;
            }
        }

        private static string? GetLinuxMachineId()
        {
            foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var content = File.ReadAllText(path).Trim();
                        if (!string.IsNullOrEmpty(content))
                        {
                            return content;
                        }
                    }
                }
                catch
                {
                    // try next path
                }
            }

            return null;
        }

        private static string? GetMacMachineId()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "ioreg",
                    Arguments = "-rd1 -c IOPlatformExpertDevice",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (process is null)
                {
                    return null;
                }

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                var match = Regex.Match(output, "\"IOPlatformUUID\"\\s*=\\s*\"([^\"]+)\"");
                return match.Success ? match.Groups[1].Value : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
