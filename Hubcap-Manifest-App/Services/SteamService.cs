using Microsoft.Win32;
using HubcapManifestApp.Helpers;
using HubcapManifestApp.Interfaces;
using HubcapManifestApp.Models;
using System;
using System.IO;

namespace HubcapManifestApp.Services
{
    public class SteamService : ISteamService
    {
        private string? _cachedSteamPath;
        private readonly SettingsService _settingsService;
        private readonly LoggerService? _logger;

        public SteamService(SettingsService settingsService, LoggerService? logger = null)
        {
            _settingsService = settingsService;
            _logger = logger;
        }

        public string? GetSteamPath()
        {
            _logger?.Debug("[SteamService] GetSteamPath() called");
            if (!string.IsNullOrEmpty(_cachedSteamPath))
            {
                _logger?.Debug($"[SteamService] Returning cached Steam path: {_cachedSteamPath}");
                return _cachedSteamPath;
            }

            // Try registry first (64-bit)
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                if (key != null)
                {
                    var installPath = key.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                    {
                        _logger?.Info($"[SteamService] Found Steam via 64-bit registry: {installPath}");
                        _cachedSteamPath = installPath;
                        return installPath;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[SteamService] Failed to read 64-bit Steam registry: {ex.Message}");
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
                if (key != null)
                {
                    var installPath = key.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                    {
                        _logger?.Info($"[SteamService] Found Steam via 32-bit registry: {installPath}");
                        _cachedSteamPath = installPath;
                        return installPath;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[SteamService] Failed to read 32-bit Steam registry: {ex.Message}");
            }

            // Fallback to common locations
            var commonPaths = new[]
            {
                @"C:\Program Files (x86)\Steam",
                @"C:\Program Files\Steam",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam")
            };

            foreach (var path in commonPaths)
            {
                if (Directory.Exists(path) && File.Exists(Path.Combine(path, SteamPaths.SteamExe)))
                {
                    _logger?.Info($"[SteamService] Found Steam via fallback path: {path}");
                    _cachedSteamPath = path;
                    return path;
                }
            }

            _logger?.Warning("[SteamService] Steam installation not found via any method");
            return null;
        }

        public string? GetStPluginPath()
        {
            var steamPath = GetSteamPath();
            if (string.IsNullOrEmpty(steamPath))
            {
                _logger?.Warning("[SteamService] GetStPluginPath: Steam path is null, returning null");
                return null;
            }

            var stpluginPath = Path.Combine(steamPath, SteamPaths.ConfigDir, SteamPaths.StPluginDir);
            _logger?.Debug($"[SteamService] stplug-in path: {stpluginPath} (exists: {Directory.Exists(stpluginPath)})");
            return stpluginPath;
        }

        public bool EnsureStPluginDirectory()
        {
            var stpluginPath = GetStPluginPath();
            if (string.IsNullOrEmpty(stpluginPath))
                return false;

            try
            {
                if (!Directory.Exists(stpluginPath))
                {
                    Directory.CreateDirectory(stpluginPath);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool ValidateSteamPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return false;

            return File.Exists(Path.Combine(path, SteamPaths.SteamExe));
        }

        public void SetCustomSteamPath(string path)
        {
            if (ValidateSteamPath(path))
            {
                _cachedSteamPath = path;
            }
        }

        public bool IsSteamRunning()
        {
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName("steam");
                var isRunning = processes.Length > 0;
                foreach (var p in processes) p.Dispose();
                return isRunning;
            }
            catch
            {
                return false;
            }
        }

        public void RestartSteam()
        {
            try
            {
                // Kill Steam
                var processes = System.Diagnostics.Process.GetProcessesByName("steam");
                foreach (var process in processes)
                {
                    using (process)
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }

                System.Threading.Thread.Sleep(2000);

                // Get settings
                var settings = _settingsService.LoadSettings();
                var steamPath = GetSteamPath();

                if (string.IsNullOrEmpty(steamPath))
                {
                    throw new Exception("Steam path not found");
                }

                var steamExe = Path.Combine(steamPath, SteamPaths.SteamExe);
                if (!File.Exists(steamExe))
                {
                    throw new Exception("steam.exe not found");
                }

                System.Diagnostics.Process.Start(steamExe);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to restart Steam: {ex.Message}", ex);
            }
        }
    }
}
