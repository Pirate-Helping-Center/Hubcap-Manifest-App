using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using HubcapManifestApp.Services.CloudRedirect;
using Microsoft.Win32;

namespace HubcapManifestApp.Services.CloudRedirect;

/// <summary>
/// Detects the Steam installation path via the Windows registry or well-known paths.
/// </summary>
public static class SteamDetector
{
    private static readonly object _cacheLock = new();
    private static string? _cachedPath;

    /// <summary>
    /// The exact Steam client version our patches and RVAs target.
    /// </summary>
    public const long ExpectedSteamVersion = 1778281814;

    /// <summary>
    /// Returns the Steam installation directory, or null if not found.
    /// Results are cached after the first successful lookup.
    /// </summary>
    public static string? FindSteamPath()
    {
        lock (_cacheLock)
        {
            if (_cachedPath != null)
                return _cachedPath;

            // Try registry (most reliable on Windows)
            _cachedPath = TryRegistry();
            if (_cachedPath != null)
                return _cachedPath;

            // Fallback: well-known paths
            _cachedPath = TryKnownPaths();
            return _cachedPath;
        }
    }

    /// <summary>
    /// Forces a re-detection on next call (useful after user changes settings).
    /// </summary>
    public static void ClearCache()
    {
        lock (_cacheLock) { _cachedPath = null; }
    }

    /// <summary>
    /// Manually override the Steam path (e.g. from a Browse dialog).
    /// Validates that the directory exists before accepting.
    /// </summary>
    public static bool SetSteamPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;
        lock (_cacheLock) { _cachedPath = path; }
        return true;
    }

    /// <summary>
    /// Reads the installed Steam client version from the manifest file.
    /// Returns null if the manifest is missing or unparseable.
    /// </summary>
    public static long? GetSteamVersion()
    {
        var steamPath = FindSteamPath();
        if (steamPath == null) return null;
        return GetSteamVersion(steamPath);
    }

    /// <summary>
    /// Reads the installed Steam client version from the manifest file at a given path.
    /// </summary>
    public static long? GetSteamVersion(string steamPath)
    {
        try
        {
            var manifest = Path.Combine(steamPath, "package", "steam_client_win64.manifest");
            if (!File.Exists(manifest)) return null;
            foreach (var line in File.ReadLines(manifest))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("\"version\""))
                    continue;
                // format: "version"		"1778281814"
                var last = trimmed.LastIndexOf('"');
                var secondLast = trimmed.LastIndexOf('"', last - 1);
                if (last > secondLast && secondLast >= 0)
                {
                    var val = trimmed[(secondLast + 1)..last];
                    if (long.TryParse(val, out var ver))
                        return ver;
                }
            }
        }
        catch
        {
            // Version parse can fail if manifest is malformed — not critical
        }
        return null;
    }

    private static string? TryRegistry()
    {
        try
        {
            // 64-bit Steam
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wow6432Node\Valve\Steam");
            var path = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                return path;

            // 32-bit Steam
            using var key32 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            path = key32?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                return path;

            // Current user
            using var keyUser = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            path = keyUser?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                return path;
        }
        catch
        {
            // Registry access can fail in sandboxed/restricted environments
        }

        return null;
    }

    private static string? TryKnownPaths()
    {
        string[] candidates =
        [
            @"C:\Games\Steam",
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            @"D:\Steam",
            @"D:\Games\Steam",
        ];

        foreach (var path in candidates)
        {
            if (Directory.Exists(path) && File.Exists(Path.Combine(path, "steam.exe")))
                return path;
        }

        return null;
    }

    /// <summary>
    /// Returns the path to the CloudRedirect config directory (%AppData%/CloudRedirect).
    /// Per-user so each Windows account has its own provider settings.
    /// </summary>
    public static string GetConfigDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CloudRedirect");
    }

    /// <summary>
    /// Returns the path to config.json (%AppData%/CloudRedirect/config.json).
    /// </summary>
    public static string GetConfigFilePath()
    {
        return Path.Combine(GetConfigDir(), "config.json");
    }

    /// <summary>
    /// Returns the path to the manifest pin config in the Steam folder
    /// (per-system, not per-user). Returns null if Steam isn't found.
    /// </summary>
    public static string? GetPinConfigPath()
    {
        var steamPath = FindSteamPath();
        if (steamPath == null) return null;
        return Path.Combine(steamPath, "cloud_redirect", "config.json");
    }

    /// <summary>
    /// Returns the log file path, or null if Steam isn't found.
    /// </summary>
    public static string? GetLogPath()
    {
        var steamPath = FindSteamPath();
        if (steamPath == null) return null;
        return Path.Combine(steamPath, "cloud_redirect.log");
    }

    /// <summary>
    /// Returns true if any Steam process is currently running.
    /// Properly disposes all Process objects to avoid native handle leaks.
    /// </summary>
    public static bool IsSteamRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("steam");
            var running = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            return running;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sends Steam the -shutdown command and polls until all Steam processes exit.
    /// Returns true if Steam exited within the timeout, false otherwise.
    /// </summary>
    /// <param name="steamPath">Path to the Steam installation directory.</param>
    /// <param name="timeoutSeconds">Maximum seconds to wait (default 30).</param>
    public static async Task<bool> ShutdownAndWaitAsync(string steamPath, int timeoutSeconds = 30)
    {
        var steamExe = Path.Combine(steamPath, "steam.exe");
        if (!File.Exists(steamExe)) return false;

        Process.Start(new ProcessStartInfo(steamExe, "-shutdown") { UseShellExecute = true })?.Dispose();

        int iterations = timeoutSeconds * 2; // 500ms per iteration
        return await Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                System.Threading.Thread.Sleep(500);
                if (!IsSteamRunning())
                    return true;
            }
            return false;
        });
    }

    /// <summary>
    /// Kills all running Steam processes. Disposes all process handles properly.
    /// </summary>
    public static void KillSteam()
    {
        try
        {
            var processes = Process.GetProcessesByName("steam");
            foreach (var p in processes)
            {
                try { p.Kill(); } catch { }
                p.Dispose();
            }
        }
        catch { }
    }

    /// <summary>
    /// Checks if Steam is running and prompts the user to close it.
    /// Returns true if Steam is not running (safe to proceed), false if the user declined or Steam is still running.
    /// </summary>
    public static async Task<bool> EnsureSteamClosedAsync()
    {
        if (!IsSteamRunning())
            return true;

        await Dialog.ShowWarningAsync(S.Get("Steam_IsRunningTitle"),
            S.Get("Steam_IsRunningMessage"));

        return false;
    }

    /// <summary>
    /// Reads and parses config.json. Returns null if file doesn't exist or can't be parsed.
    /// </summary>
    public static CloudConfig? ReadConfig()
    {
        var configPath = GetConfigFilePath();
        if (!File.Exists(configPath)) return null;

        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("provider", out var providerProp))
                return null;
            var provider = providerProp.GetString();
            if (provider == null) return null;

            string? tokenPath = null;
            if (root.TryGetProperty("token_path", out var tp))
                tokenPath = tp.GetString();

            string? syncPath = null;
            if (root.TryGetProperty("sync_path", out var sp))
                syncPath = sp.GetString();

            return new CloudConfig(provider, tokenPath, syncPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the current mode setting ("cloud_redirect" or "stfixer") from settings.json.
    /// Returns null if the file doesn't exist or the mode property is missing.
    /// </summary>
    public static string? ReadModeSetting()
    {
        try
        {
            var path = Path.Combine(GetConfigDir(), "settings.json");
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("mode", out var prop))
                return prop.GetString();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ReadModeSetting: {ex.Message}");
        }
        return null;
    }
}

/// <summary>
/// Parsed contents of cloud_redirect/config.json.
/// </summary>
public record CloudConfig(string Provider, string? TokenPath, string? SyncPath)
{
    public string DisplayName => Provider switch
    {
        "gdrive" => "Google Drive",
        "onedrive" => "OneDrive",
        "folder" => "Folder / Network Drive",
        "local" => "Local Only",
        _ => Provider
    };

    public bool IsOAuth => Provider is "gdrive" or "onedrive";
    public bool IsFolder => Provider == "folder";
    public bool IsLocal => Provider == "local";
}
