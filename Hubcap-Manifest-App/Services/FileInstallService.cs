using HubcapManifestApp.Helpers;
using HubcapManifestApp.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace HubcapManifestApp.Services
{
    public class FileInstallService
    {
        private readonly SteamService _steamService;
        private readonly LoggerService _logger;

        public FileInstallService(SteamService steamService, LoggerService logger)
        {
            _steamService = steamService;
            _logger = logger;
        }

        public async Task<Dictionary<string, string>> InstallFromZipAsync(string zipPath, Action<string>? progressCallback = null)
        {
            var depotKeys = new Dictionary<string, string>();

            try
            {
                progressCallback?.Invoke("Extracting ZIP file...");

                var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempDir);

                try
                {
                    await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tempDir));

                    progressCallback?.Invoke("Installing files...");

                    var luaFiles = Directory.GetFiles(tempDir, "*.lua", SearchOption.AllDirectories);
                    var manifestFiles = Directory.GetFiles(tempDir, "*.manifest", SearchOption.AllDirectories);

                    if (luaFiles.Length == 0)
                    {
                        throw new Exception("No .lua files found in ZIP");
                    }

                    _logger.Info("Installing .lua files to stplug-in");
                    var stpluginPath = _steamService.GetStPluginPath();
                    if (string.IsNullOrEmpty(stpluginPath))
                    {
                        _logger.Error("Steam installation not found - stpluginPath is null or empty");
                        throw new Exception("Steam installation not found");
                    }

                    _logger.Info($"stplug-in path: {stpluginPath}");
                    _steamService.EnsureStPluginDirectory();

                    foreach (var luaFile in luaFiles)
                    {
                        var fileName = Path.GetFileName(luaFile);
                        var destPath = Path.Combine(stpluginPath, fileName);

                        progressCallback?.Invoke($"Installing {fileName}...");
                        _logger.Info($"Installing {fileName} to: {destPath}");

                        if (File.Exists(destPath))
                        {
                            File.Delete(destPath);
                        }

                        var disabledPath = destPath + ".disabled";
                        if (File.Exists(disabledPath))
                        {
                            File.Delete(disabledPath);
                        }

                        File.Copy(luaFile, destPath, true);
                        _logger.Info($"Successfully installed: {fileName}");
                    }

                    var steamPath = _steamService.GetSteamPath();
                    if (!string.IsNullOrEmpty(steamPath) && manifestFiles.Length > 0)
                    {
                        var depotCachePath = Path.Combine(steamPath, SteamPaths.DepotCacheDir);
                        Directory.CreateDirectory(depotCachePath);

                        foreach (var manifestFile in manifestFiles)
                        {
                            var fileName = Path.GetFileName(manifestFile);
                            var destPath = Path.Combine(depotCachePath, fileName);

                            progressCallback?.Invoke($"Installing {fileName}...");

                            if (File.Exists(destPath))
                            {
                                File.Delete(destPath);
                            }

                            File.Copy(manifestFile, destPath, true);
                        }
                    }

                    progressCallback?.Invoke("Installation complete!");

                    return depotKeys;
                }
                finally
                {
                    try
                    {
                        Directory.Delete(tempDir, true);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                progressCallback?.Invoke($"Error: {ex.Message}");
                throw new Exception($"Installation failed: {ex.Message}", ex);
            }
        }

        public async Task<bool> InstallLuaFileAsync(string luaPath)
        {
            try
            {
                _logger.Info($"InstallLuaFileAsync called with: {luaPath}");

                var stpluginPath = _steamService.GetStPluginPath();
                if (string.IsNullOrEmpty(stpluginPath))
                {
                    _logger.Error("Steam installation not found - stpluginPath is null or empty");
                    throw new Exception("Steam installation not found");
                }

                _logger.Info($"stplug-in path: {stpluginPath}");

                _steamService.EnsureStPluginDirectory();
                _logger.Debug("Ensured stplug-in directory exists");

                var fileName = Path.GetFileName(luaPath);
                var destPath = Path.Combine(stpluginPath, fileName);
                _logger.Info($"Installing lua file to: {destPath}");

                // Remove existing file
                if (File.Exists(destPath))
                {
                    _logger.Debug($"Removing existing file: {destPath}");
                    File.Delete(destPath);
                }

                // Remove .disabled version
                var disabledPath = destPath + ".disabled";
                if (File.Exists(disabledPath))
                {
                    _logger.Debug($"Removing disabled file: {disabledPath}");
                    File.Delete(disabledPath);
                }

                // Copy file
                _logger.Debug($"Copying {luaPath} to {destPath}");
                await Task.Run(() => File.Copy(luaPath, destPath, true));
                _logger.Info($"Successfully installed lua file: {fileName}");

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Installation failed: {ex.Message}");
                throw new Exception($"Installation failed: {ex.Message}", ex);
            }
        }

        public async Task<bool> InstallManifestFileAsync(string manifestPath)
        {
            try
            {
                var steamPath = _steamService.GetSteamPath();
                if (string.IsNullOrEmpty(steamPath))
                {
                    throw new Exception("Steam installation not found");
                }

                // Manifest files go to depotcache
                var depotCachePath = Path.Combine(steamPath, SteamPaths.DepotCacheDir);
                Directory.CreateDirectory(depotCachePath);

                var fileName = Path.GetFileName(manifestPath);
                var destPath = Path.Combine(depotCachePath, fileName);

                // Remove existing file
                if (File.Exists(destPath))
                {
                    File.Delete(destPath);
                }

                // Copy file
                await Task.Run(() => File.Copy(manifestPath, destPath, true));

                return true;
            }
            catch (Exception ex)
            {
                throw new Exception($"Installation failed: {ex.Message}", ex);
            }
        }

        public List<Game> GetInstalledGames()
        {
            _logger.Debug("[FileInstallService] GetInstalledGames() called");
            var games = new List<Game>();

            try
            {
                var stpluginPath = _steamService.GetStPluginPath();
                _logger.Debug($"[FileInstallService] stplug-in path: {stpluginPath ?? "null"}");

                if (string.IsNullOrEmpty(stpluginPath) || !Directory.Exists(stpluginPath))
                {
                    _logger.Warning($"[FileInstallService] stplug-in path is null/empty or does not exist (exists: {(stpluginPath != null ? Directory.Exists(stpluginPath).ToString() : "N/A")})");
                    return games;
                }

                var luaFiles = Directory.GetFiles(stpluginPath, "*.lua");
                _logger.Debug($"[FileInstallService] Found {luaFiles.Length} .lua file(s) in stplug-in");

                foreach (var luaFile in luaFiles)
                {
                    var fileName = Path.GetFileName(luaFile);
                    var appId = Path.GetFileNameWithoutExtension(fileName);
                    _logger.Debug($"[FileInstallService] Processing lua file: {fileName} (appId={appId})");

                    var fileInfo = new FileInfo(luaFile);

                    games.Add(new Game
                    {
                        AppId = appId,
                        Name = appId,
                        IsInstalled = true,
                        LocalPath = luaFile,
                        SizeBytes = fileInfo.Length,
                        InstallDate = fileInfo.CreationTime,
                        LastUpdated = fileInfo.LastWriteTime
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[FileInstallService] Error scanning installed games: {ex.Message}\n{ex.StackTrace}");
            }

            _logger.Info($"[FileInstallService] Returning {games.Count} installed game(s)");
            return games;
        }

        /// <summary>
        /// Removes a lua-registered game. By default asks Steam to uninstall the game
        /// AND deletes the .lua file from stplug-in, matching the original behavior.
        /// Pass <paramref name="uninstallFromSteam"/>=false to skip the Steam call (the
        /// "keep the game but drop the lua" flow never needs this — the game is already
        /// installed in Steam and that's fine). Pass <paramref name="deleteLua"/>=false
        /// to keep the .lua file in place (the "wipe the game, keep the lua" flow).
        /// </summary>
        public bool UninstallGame(string appId, bool uninstallFromSteam = true, bool deleteLua = true)
        {
            try
            {
                var stpluginPath = _steamService.GetStPluginPath();
                if (string.IsNullOrEmpty(stpluginPath))
                {
                    return false;
                }

                var luaPath = Path.Combine(stpluginPath, $"{appId}.lua");
                var disabledPath = Path.Combine(stpluginPath, $"{appId}.lua.disabled");

                if (uninstallFromSteam)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = $"steam://uninstall/{appId}",
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"Steam uninstall command failed (continuing anyway): {ex.Message}");
                    }
                }

                if (deleteLua)
                {
                    if (File.Exists(luaPath)) File.Delete(luaPath);
                    if (File.Exists(disabledPath)) File.Delete(disabledPath);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to uninstall game {appId}: {ex.Message}");
                return false;
            }
        }

    }
}
