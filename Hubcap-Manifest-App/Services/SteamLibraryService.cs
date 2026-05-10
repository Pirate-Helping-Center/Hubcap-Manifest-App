using HubcapManifestApp.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HubcapManifestApp.Services
{
    public class SteamLibraryService
    {
        private readonly SteamService _steamService;
        private readonly LoggerService? _logger;

        public SteamLibraryService(SteamService steamService, LoggerService? logger = null)
        {
            _steamService = steamService;
            _logger = logger;
        }

        public List<string> GetLibraryFolders()
        {
            _logger?.Debug("[SteamLibraryService] GetLibraryFolders() called");
            var libraryFolders = new List<string>();

            try
            {
                var steamPath = _steamService.GetSteamPath();
                _logger?.Debug($"[SteamLibraryService] Steam path: {steamPath ?? "null"}");
                if (string.IsNullOrEmpty(steamPath))
                {
                    _logger?.Warning("[SteamLibraryService] Steam path is null/empty, returning empty list");
                    return libraryFolders;
                }

                var mainLibraryPath = Path.Combine(steamPath, SteamPaths.SteamAppsDir);
                var mainExists = Directory.Exists(mainLibraryPath);
                _logger?.Debug($"[SteamLibraryService] Main library path: {mainLibraryPath} (exists: {mainExists})");
                if (mainExists)
                {
                    libraryFolders.Add(mainLibraryPath);
                }

                var libraryFoldersPath = Path.Combine(steamPath, SteamPaths.SteamAppsDir, SteamPaths.LibraryFoldersVdf);
                _logger?.Debug($"[SteamLibraryService] VDF file: {libraryFoldersPath} (exists: {File.Exists(libraryFoldersPath)})");
                if (!File.Exists(libraryFoldersPath))
                {
                    _logger?.Warning("[SteamLibraryService] libraryfolders.vdf not found");
                    return libraryFolders;
                }

                var content = File.ReadAllText(libraryFoldersPath);
                var additionalPaths = ParseLibraryFoldersVdf(content);
                _logger?.Debug($"[SteamLibraryService] Found {additionalPaths.Count} additional library path(s) from VDF");
                libraryFolders.AddRange(additionalPaths);
            }
            catch (Exception ex)
            {
                _logger?.Error($"[SteamLibraryService] Error getting library folders: {ex.Message}\n{ex.StackTrace}");
            }

            var result = libraryFolders.Distinct().ToList();
            _logger?.Info($"[SteamLibraryService] Returning {result.Count} library folder(s)");
            return result;
        }

        private List<string> ParseLibraryFoldersVdf(string content)
        {
            var libraryPaths = new List<string>();

            try
            {
                var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                _logger?.Debug($"[SteamLibraryService] Parsing VDF content ({lines.Length} lines)");

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Contains("\"path\""))
                    {
                        var parts = trimmed.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            var pathValue = parts[parts.Length - 1].Trim('"');
                            pathValue = pathValue.Replace("\\\\", "\\");
                            var steamappsPath = Path.Combine(pathValue, SteamPaths.SteamAppsDir);
                            var exists = Directory.Exists(steamappsPath);
                            _logger?.Debug($"[SteamLibraryService] VDF path entry: {steamappsPath} (exists: {exists})");
                            if (exists)
                            {
                                libraryPaths.Add(steamappsPath);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warning($"[SteamLibraryService] Failed to parse VDF content: {ex.Message}");
            }

            return libraryPaths;
        }
    }
}
