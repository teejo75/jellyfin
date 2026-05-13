using System;
using System.Globalization;
using System.IO;
using MediaBrowser.Common.Net;

namespace Jellyfin.ProxyConfig;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 1;
    private const int ExitError = 2;

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return ExitUsage;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args[1..];

        try
        {
            return command switch
            {
                "show" => Show(rest),
                "set" => Set(rest),
                "clear" => Clear(rest),
                "help" or "-h" or "--help" => PrintUsageAndOk(),
                _ => UnknownCommand(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitError;
        }
    }

    private static int Show(string[] args)
    {
        var configDir = ResolveConfigDir(args, out _);
        var path = ProxySettingsStore.GetFilePath(configDir);
        var settings = ProxySettingsStore.Load(configDir);

        Console.WriteLine($"Config file:        {path}");
        Console.WriteLine($"Exists:             {File.Exists(path)}");
        Console.WriteLine($"Enabled:            {settings.Enabled}");
        Console.WriteLine($"Address:            {settings.Address}");
        Console.WriteLine($"Port:               {settings.Port}");
        Console.WriteLine($"UseHttps:           {settings.UseHttps}");
        Console.WriteLine($"Username:           {settings.Username}");
        Console.WriteLine($"Password encrypted: {!string.IsNullOrEmpty(settings.EncryptedPassword)}");
        return ExitOk;
    }

    private static int Set(string[] args)
    {
        var configDir = ResolveConfigDir(args, out var positional);
        var settings = ProxySettingsStore.Load(configDir);

        bool? enabled = null;
        bool? useHttps = null;
        string? address = null;
        int? port = null;
        string? user = null;
        bool clearPassword = false;

        for (var i = 0; i < positional.Count; i++)
        {
            var key = positional[i].TrimStart('-').ToLowerInvariant();
            switch (key)
            {
                case "address": address = RequireValue(positional, ref i, key); break;
                case "port": port = int.Parse(RequireValue(positional, ref i, key), CultureInfo.InvariantCulture); break;
                case "user" or "username": user = RequireValue(positional, ref i, key); break;
                case "no-password": clearPassword = true; break;
                case "https": useHttps = true; break;
                case "no-https": useHttps = false; break;
                case "enable": enabled = true; break;
                case "disable": enabled = false; break;
                default:
                    Console.Error.WriteLine($"Unknown option: {positional[i]}");
                    return ExitUsage;
            }
        }

        if (address is not null) settings.Address = address;
        if (port is int p) settings.Port = p;
        if (useHttps is bool h) settings.UseHttps = h;
        if (user is not null)
        {
            settings.Username = user;

            // Whenever a username is provided, prompt for the matching password.
            // Passing the password on the command line would leak it via process
            // listings and shell history.
            var pw = PromptForPasswordWithConfirmation();
            if (pw is null)
            {
                Console.Error.WriteLine("Aborted: no password entered.");
                return ExitUsage;
            }

            settings.EncryptedPassword = ProxyEncryption.Protect(pw);
        }

        if (clearPassword) settings.EncryptedPassword = null;
        if (enabled is bool e) settings.Enabled = e;
        else if (settings.Enabled is false && address is not null) settings.Enabled = true;

        ProxySettingsStore.Save(configDir, settings);
        Console.WriteLine($"Saved {ProxySettingsStore.GetFilePath(configDir)}");
        Console.WriteLine("Restart the Jellyfin service for changes to take effect.");
        return ExitOk;
    }

    private static int Clear(string[] args)
    {
        var configDir = ResolveConfigDir(args, out _);
        var path = ProxySettingsStore.GetFilePath(configDir);
        if (File.Exists(path))
        {
            File.Delete(path);
            Console.WriteLine($"Deleted {path}");
        }
        else
        {
            Console.WriteLine("No proxy configuration to clear.");
        }

        Console.WriteLine("Restart the Jellyfin service for changes to take effect.");
        return ExitOk;
    }

    private static string ResolveConfigDir(string[] args, out List<string> remaining)
    {
        remaining = new List<string>();
        string? overridden = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config-dir" && i + 1 < args.Length)
            {
                overridden = args[i + 1];
                i++;
                continue;
            }

            remaining.Add(args[i]);
        }

        var dir = overridden
            ?? Environment.GetEnvironmentVariable("JELLYFIN_CONFIG_DIR")
            ?? DefaultConfigDir();

        return Path.GetFullPath(dir);
    }

    private static string DefaultConfigDir()
    {
        var dataDir = Environment.GetEnvironmentVariable("JELLYFIN_DATA_DIR")
            ?? Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
                "jellyfin");
        return Path.Join(dataDir, "config");
    }

    private static string RequireValue(List<string> args, ref int i, string key)
    {
        if (i + 1 >= args.Count)
        {
            throw new ArgumentException($"Option --{key} requires a value");
        }

        i++;
        return args[i];
    }

    private static int UnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"Unknown command: {cmd}");
        PrintUsage();
        return ExitUsage;
    }

    private static int PrintUsageAndOk()
    {
        PrintUsage();
        return ExitOk;
    }

    /// <summary>
    /// Prompts twice for a password with no echo. Reprompts from the beginning
    /// if the two entries don't match. Returns null if the user enters an empty
    /// password twice in a row (i.e. abort).
    /// </summary>
    private static string? PromptForPasswordWithConfirmation()
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Password prompt requires an interactive console (stdin is redirected).");
            return null;
        }

        while (true)
        {
            Console.Write("Proxy password: ");
            var first = ReadHiddenLine();
            if (first.Length == 0)
            {
                return null;
            }

            Console.Write("Confirm password: ");
            var second = ReadHiddenLine();

            if (string.Equals(first, second, StringComparison.Ordinal))
            {
                return first;
            }

            Console.WriteLine("Passwords do not match. Try again.");
        }
    }

    /// <summary>
    /// Reads a line from the console without echoing characters. Backspace edits.
    /// Enter terminates.
    /// </summary>
    private static string ReadHiddenLine()
    {
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buffer.ToString();
                case ConsoleKey.Backspace:
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                    }

                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Append(key.KeyChar);
                    }

                    break;
            }
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            jellyfin-proxyconfig — manage Jellyfin outbound HTTP proxy settings.

            Usage:
              jellyfin-proxyconfig show   [--config-dir DIR]
              jellyfin-proxyconfig set    [options]   [--config-dir DIR]
              jellyfin-proxyconfig clear  [--config-dir DIR]

            'set' options (all optional; only provided keys are updated):
              --address HOST          Proxy host (e.g. proxy.example.com)
              --port N                Proxy port
              --user NAME             Username for proxy auth. Prompts (twice, no echo)
                                      for the matching password.
              --no-password           Remove stored password
              --https / --no-https    Use https:// scheme to reach proxy
              --enable / --disable    Toggle whether the proxy is used

            The password is encrypted at rest with AES-256-GCM using a key derived from
            this machine's identifier. Encrypted blobs cannot be moved between hosts.

            Config dir resolution (in order):
              --config-dir argument
              JELLYFIN_CONFIG_DIR env var
              $JELLYFIN_DATA_DIR/config
              <platform local app data>/jellyfin/config
            """);
    }
}
