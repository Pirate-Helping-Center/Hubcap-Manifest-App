using HubcapManifestApp.Helpers;
using HubcapManifestApp.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HubcapManifestApp.Services
{
    public class SteamGamesService
    {
        private readonly SteamService _steamService;
        private readonly LoggerService? _logger;

        public SteamGamesService(SteamService steamService, LoggerService? logger = null)
        {
            _steamService = steamService;
            _logger = logger;
        }

        public List<SteamGame> GetInstalledGames()
        {
            _logger?.Info("[SteamGamesService] ========== GetInstalledGames START ==========");
            var games = new List<SteamGame>();

            try
            {
                var libraryFolders = GetLibraryFolders();
                _logger?.Info($"[SteamGamesService] Found {libraryFolders.Count} library folder(s): [{string.Join(", ", libraryFolders)}]");

                foreach (var libraryPath in libraryFolders)
                {
                    var steamappsPath = Path.Combine(libraryPath, SteamPaths.SteamAppsDir);
                    _logger?.Info($"[SteamGamesService] Checking {steamappsPath} (exists: {Directory.Exists(steamappsPath)})");
                    if (!Directory.Exists(steamappsPath))
                    {
                        _logger?.Warning($"[SteamGamesService] steamapps directory does not exist: {steamappsPath}");
                        continue;
                    }

                    var manifestFiles = Directory.GetFiles(steamappsPath, "appmanifest_*.acf");
                    _logger?.Info($"[SteamGamesService] Found {manifestFiles.Length} manifest(s) in {steamappsPath}");

                    foreach (var manifestFile in manifestFiles)
                    {
                        try
                        {
                            var game = ParseAppManifest(manifestFile, libraryPath);
                            if (game != null)
                            {
                                _logger?.Info($"[SteamGamesService]   parsed: AppId='{game.AppId}' Name='{game.Name}' StateFlags='{game.StateFlags}'");
                                games.Add(game);
                            }
                            else
                            {
                                _logger?.Warning($"[SteamGamesService]   NULL from ParseAppManifest for {Path.GetFileName(manifestFile)}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.Warning($"[SteamGamesService] Failed to parse manifest {manifestFile}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error($"[SteamGamesService] GetInstalledGames failed: {ex.Message}\n{ex.StackTrace}");
            }

            var uniqueGames = games.GroupBy(g => g.AppId)
                       .Select(g => g.First())
                       .OrderBy(g => g.Name)
                       .ToList();
            _logger?.Info($"[SteamGamesService] ========== GetInstalledGames END: returning {uniqueGames.Count} unique games (from {games.Count} total) ==========");
            return uniqueGames;
        }

        private List<string> GetLibraryFolders()
        {
            _logger?.Debug("[SteamGamesService] GetLibraryFolders() called");
            var folders = new List<string>();

            var steamPath = _steamService.GetSteamPath();
            _logger?.Debug($"[SteamGamesService] Steam path: {steamPath ?? "null"}");
            if (string.IsNullOrEmpty(steamPath))
            {
                _logger?.Error("[SteamGamesService] Steam installation not found");
                throw new Exception("Steam installation not found");
            }

            folders.Add(steamPath);

            var libraryFoldersFile = Path.Combine(steamPath, SteamPaths.SteamAppsDir, SteamPaths.LibraryFoldersVdf);
            if (!File.Exists(libraryFoldersFile))
            {
                _logger?.Debug($"[SteamGamesService] VDF not found at steamapps, trying config location");
                libraryFoldersFile = Path.Combine(steamPath, SteamPaths.ConfigDir, SteamPaths.LibraryFoldersVdf);
            }
            _logger?.Debug($"[SteamGamesService] Using VDF file: {libraryFoldersFile} (exists: {File.Exists(libraryFoldersFile)})");

            if (File.Exists(libraryFoldersFile))
            {
                try
                {
                    var data = VdfParser.Parse(libraryFoldersFile);
                    var libraryFoldersObj = VdfParser.GetObject(data, "libraryfolders");

                    if (libraryFoldersObj != null)
                    {
                        var directPath = VdfParser.GetValue(libraryFoldersObj, "path");
                        if (!string.IsNullOrEmpty(directPath))
                        {
                            var exists = Directory.Exists(directPath);
                            _logger?.Debug($"[SteamGamesService] Direct path: {directPath} (exists: {exists})");
                            if (exists) folders.Add(directPath);
                        }

                        for (int i = 0; i < 10; i++)
                        {
                            var folderData = VdfParser.GetObject(libraryFoldersObj, i.ToString());
                            if (folderData != null)
                            {
                                var path = VdfParser.GetValue(folderData, "path");
                                if (!string.IsNullOrEmpty(path))
                                {
                                    var exists = Directory.Exists(path);
                                    _logger?.Debug($"[SteamGamesService] Library folder [{i}]: {path} (exists: {exists})");
                                    if (exists) folders.Add(path);
                                }
                            }
                        }
                    }
                    else
                    {
                        _logger?.Warning("[SteamGamesService] libraryfolders object is null in VDF");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warning($"[SteamGamesService] Failed to parse libraryfolders.vdf: {ex.Message}");
                }
            }

            var result = folders.Distinct().ToList();
            _logger?.Info($"[SteamGamesService] Resolved {result.Count} library folder(s)");
            return result;
        }

        private SteamGame? ParseAppManifest(string manifestPath, string libraryPath)
        {
            _logger?.Debug($"[SteamGamesService] Parsing manifest: {manifestPath}");
            try
            {
                var data = VdfParser.Parse(manifestPath);
                var appState = VdfParser.GetObject(data, "AppState");

                if (appState == null)
                {
                    _logger?.Warning($"[SteamGamesService] AppState is null in {manifestPath}");
                    return null;
                }

                var appId = VdfParser.GetValue(appState, "appid");
                var name = VdfParser.GetValue(appState, "name");
                var installDir = VdfParser.GetValue(appState, "installdir");

                // Try multiple size fields - Steam uses different ones depending on state
                var sizeOnDisk = VdfParser.GetLong(appState, "SizeOnDisk");
                if (sizeOnDisk == 0)
                {
                    sizeOnDisk = VdfParser.GetLong(appState, "BytesDownloaded");
                }
                if (sizeOnDisk == 0)
                {
                    sizeOnDisk = VdfParser.GetLong(appState, "BytesToDownload");
                }

                var lastUpdated = VdfParser.GetLong(appState, "LastUpdated");
                var stateFlags = VdfParser.GetValue(appState, "StateFlags");
                var buildId = VdfParser.GetValue(appState, "buildid");

                if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name))
                {
                    _logger?.Warning($"[SteamGamesService] Missing appId or name in {manifestPath} (appId={appId}, name={name})");
                    return null;
                }

                var gamePath = !string.IsNullOrEmpty(installDir)
                    ? Path.Combine(libraryPath, SteamPaths.SteamAppsDir, SteamPaths.CommonDir, installDir)
                    : Path.Combine(libraryPath, SteamPaths.SteamAppsDir, SteamPaths.CommonDir, name);

                // If size is still 0 and game is installed, try calculating from folder
                if (sizeOnDisk == 0 && Directory.Exists(gamePath))
                {
                    try
                    {
                        sizeOnDisk = CalculateFolderSize(gamePath);
                    }
                    catch
                    {
                        // Fallback failed, keep 0
                    }
                }

                var game = new SteamGame
                {
                    AppId = appId,
                    Name = name,
                    InstallDir = installDir,
                    SizeOnDisk = sizeOnDisk,
                    LastUpdated = lastUpdated > 0 ? DateTimeOffset.FromUnixTimeSeconds(lastUpdated).DateTime : null,
                    LibraryPath = gamePath,
                    StateFlags = stateFlags,
                    IsFullyInstalled = stateFlags == "4", // StateFlag 4 = Fully Installed
                    BuildId = buildId
                };

                return game;
            }
            catch (Exception ex)
            {
                _logger?.Warning($"[SteamGamesService] Exception parsing manifest {manifestPath}: {ex.Message}");
                return null;
            }
        }

        public string? GetLocalIconPath(string appId)
        {
            var steamPath = _steamService.GetSteamPath();
            if (string.IsNullOrEmpty(steamPath))
                return null;

            var appcachePath = Path.Combine(steamPath, "appcache", "librarycache");
            if (!Directory.Exists(appcachePath))
                return null;

            // Try different icon formats Steam uses
            var iconFormats = new[]
            {
                $"{appId}_library_600x900.jpg",
                $"{appId}_library_600x900_2x.jpg",
                $"{appId}_icon.jpg",
                $"{appId}_logo.png",
                $"{appId}_header.jpg"
            };

            foreach (var format in iconFormats)
            {
                var iconPath = Path.Combine(appcachePath, format);
                if (File.Exists(iconPath))
                {
                    return iconPath;
                }
            }

            return null;
        }

        public string GetSteamCdnIconUrl(string appId)
        {
            // Primary format: library_600x900 (vertical poster)
            return $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg";
        }

        public bool UninstallGame(string appId)
        {
            try
            {
                // Use Steam's uninstall protocol - this is safer and lets Steam handle cleanup
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = $"steam://uninstall/{appId}",
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static long CalculateFolderSize(string folderPath)
        {
            if (!Directory.Exists(folderPath))
                return 0;

            long totalSize = 0;

            try
            {
                // Calculate size of all files
                var files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        totalSize += fileInfo.Length;
                    }
                    catch
                    {
                        // Skip files we can't access
                    }
                }
            }
            catch
            {
                // Return what we have so far
            }

            return totalSize;
        }
    }
}
