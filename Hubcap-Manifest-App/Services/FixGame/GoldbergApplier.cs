using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HubcapManifestApp.Services.FixGame
{
    /// <summary>
    /// Applies Goldberg Steam Emulator to a game directory.
    /// Replaces steam_api(64).dll with Goldberg's, backs up originals.
    /// </summary>
    public class GoldbergApplier
    {
        private readonly FixGameCacheService _cache;
        private readonly LoggerService _logger;

        public event Action<string>? Log;

        public GoldbergApplier(FixGameCacheService cache)
        {
            _cache = cache;
            _logger = new LoggerService("GoldbergApplier");
        }

        /// <summary>
        /// Scans a game directory and applies Goldberg emulator.
        /// Returns (success, message).
        /// </summary>
        public (bool success, string message) Apply(string gameDir)
        {
            try
            {
                if (!_cache.HasGoldbergDlls())
                    return (false, "Goldberg DLLs not downloaded. Run updater first.");

                // Find steam_api DLLs in the game directory
                var api32Files = Directory.GetFiles(gameDir, "steam_api.dll", SearchOption.AllDirectories);
                var api64Files = Directory.GetFiles(gameDir, "steam_api64.dll", SearchOption.AllDirectories);

                if (api32Files.Length == 0 && api64Files.Length == 0)
                    return (false, "No steam_api.dll or steam_api64.dll found in game directory");

                int replaced = 0;

                foreach (var apiFile in api32Files)
                {
                    if (ReplaceWithGoldberg(apiFile, false))
                        replaced++;
                }

                foreach (var apiFile in api64Files)
                {
                    if (ReplaceWithGoldberg(apiFile, true))
                        replaced++;
                }

                return (replaced > 0,
                    replaced > 0
                        ? $"Replaced {replaced} steam_api DLL(s) with Goldberg emulator"
                        : "No DLLs were replaced");
            }
            catch (Exception ex)
            {
                _logger.Error($"Apply error: {ex}");
                return (false, $"Apply failed: {ex.Message}");
            }
        }

        private static readonly string[] InterfaceNames = new[]
        {
            "SteamClient",
            "SteamGameServer",
            "SteamGameServerStats",
            "SteamUser",
            "SteamFriends",
            "SteamUtils",
            "SteamMatchMaking",
            "SteamMatchMakingServers",
            "STEAMUSERSTATS_INTERFACE_VERSION",
            "STEAMAPPS_INTERFACE_VERSION",
            "SteamNetworking",
            "STEAMREMOTESTORAGE_INTERFACE_VERSION",
            "STEAMSCREENSHOTS_INTERFACE_VERSION",
            "STEAMHTTP_INTERFACE_VERSION",
            "STEAMUNIFIEDMESSAGES_INTERFACE_VERSION",
            "STEAMUGC_INTERFACE_VERSION",
            "STEAMAPPLIST_INTERFACE_VERSION",
            "STEAMMUSIC_INTERFACE_VERSION",
            "STEAMMUSICREMOTE_INTERFACE_VERSION",
            "STEAMHTMLSURFACE_INTERFACE_VERSION_",
            "STEAMINVENTORY_INTERFACE_V",
            "SteamController",
            "SteamMasterServerUpdater",
            "STEAMVIDEO_INTERFACE_V"
        };

        /// <summary>
        /// Scans the original steam_api DLL for interface version strings and writes steam_interfaces.txt.
        /// Must be called BEFORE replacing the DLL.
        /// </summary>
        private void GenerateInterfacesFile(string dllPath, string settingsDir)
        {
            try
            {
                var content = File.ReadAllText(dllPath);
                var interfaces = new HashSet<string>();

                foreach (var name in InterfaceNames)
                {
                    foreach (Match m in Regex.Matches(content, $@"{name}\d{{3}}"))
                        interfaces.Add(m.Value);
                }

                // Special case for SteamController
                if (!interfaces.Any(i => i.StartsWith("STEAMCONTROLLER")))
                {
                    foreach (Match m in Regex.Matches(content, @"STEAMCONTROLLER_INTERFACE_VERSION\d{3}"))
                        interfaces.Add(m.Value);
                    if (!interfaces.Any(i => i.StartsWith("STEAMCONTROLLER")))
                    {
                        foreach (Match m in Regex.Matches(content, "STEAMCONTROLLER_INTERFACE_VERSION"))
                            interfaces.Add(m.Value);
                    }
                }

                if (interfaces.Count > 0)
                {
                    Directory.CreateDirectory(settingsDir);
                    var interfacesPath = Path.Combine(settingsDir, "steam_interfaces.txt");
                    File.WriteAllText(interfacesPath, string.Join("\n", interfaces.OrderBy(x => x)));
                    LogMsg($"  Generated steam_interfaces.txt ({interfaces.Count} interfaces)");
                }
                else
                {
                    LogMsg($"  No interfaces found in {Path.GetFileName(dllPath)} (newer game, not needed)");
                }
            }
            catch (Exception ex)
            {
                LogMsg($"  Failed to generate interfaces: {ex.Message}");
            }
        }

        private bool ReplaceWithGoldberg(string targetPath, bool is64Bit)
        {
            try
            {
                var backupPath = targetPath + ".bak";

                // Generate interfaces file from original DLL before replacing
                var settingsDir = Path.Combine(Path.GetDirectoryName(targetPath) ?? "", "steam_settings");
                GenerateInterfacesFile(targetPath, settingsDir);

                // Don't replace if already Goldberg (check if backup exists and is same size)
                if (File.Exists(backupPath))
                {
                    LogMsg($"  Backup already exists: {Path.GetFileName(backupPath)}");
                }
                else
                {
                    // Backup original
                    File.Copy(targetPath, backupPath, false);
                    LogMsg($"  Backed up: {Path.GetFileName(targetPath)} -> .bak");
                }

                // Copy Goldberg DLL
                var goldbergDll = _cache.GetSteamApiDllPath(is64Bit);
                File.Copy(goldbergDll, targetPath, true);
                LogMsg($"  Replaced: {Path.GetFileName(targetPath)} ({(is64Bit ? "64-bit" : "32-bit")})");

                return true;
            }
            catch (Exception ex)
            {
                LogMsg($"  Failed to replace {Path.GetFileName(targetPath)}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Detects if a game directory has steam_api DLLs and returns info about them.
        /// </summary>
        public static (bool has32, bool has64, string[] paths) DetectSteamApi(string gameDir)
        {
            var api32 = Directory.GetFiles(gameDir, "steam_api.dll", SearchOption.AllDirectories);
            var api64 = Directory.GetFiles(gameDir, "steam_api64.dll", SearchOption.AllDirectories);
            return (api32.Length > 0, api64.Length > 0, api32.Concat(api64).ToArray());
        }

        /// <summary>
        /// Restores original DLLs from backups.
        /// </summary>
        public (bool success, string message) Restore(string gameDir)
        {
            try
            {
                var bakFiles = Directory.GetFiles(gameDir, "steam_api*.dll.bak", SearchOption.AllDirectories);
                if (bakFiles.Length == 0)
                    return (false, "No backups found to restore");

                int restored = 0;
                foreach (var bakFile in bakFiles)
                {
                    var originalPath = bakFile[..^4]; // Remove .bak
                    File.Copy(bakFile, originalPath, true);
                    File.Delete(bakFile);
                    restored++;
                    LogMsg($"  Restored: {Path.GetFileName(originalPath)}");
                }

                // Clean up steam_settings folder
                var settingsDir = Path.Combine(gameDir, "steam_settings");
                if (Directory.Exists(settingsDir))
                {
                    Directory.Delete(settingsDir, true);
                    LogMsg("  Removed steam_settings/");
                }

                var appIdFile = Path.Combine(gameDir, "steam_appid.txt");
                if (File.Exists(appIdFile))
                {
                    File.Delete(appIdFile);
                    LogMsg("  Removed steam_appid.txt");
                }

                return (true, $"Restored {restored} original DLL(s)");
            }
            catch (Exception ex)
            {
                return (false, $"Restore failed: {ex.Message}");
            }
        }

        private void LogMsg(string msg)
        {
            _logger.Info(msg);
            Log?.Invoke(msg);
        }
    }
}
