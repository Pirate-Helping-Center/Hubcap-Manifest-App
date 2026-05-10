using HubcapManifestApp.Helpers;
using HubcapManifestApp.Models;
using HubcapManifestApp.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;

namespace HubcapManifestApp.ViewModels
{
    // Game action commands: uninstall, install, refetch, fix, run, details, explorer, etc.
    public partial class LibraryViewModel
    {
        [RelayCommand]
        private async Task UninstallItem(LibraryItem item)
        {
            if (item == null) return;

            try
            {
                bool success = false;
                bool removeFromLibrary = true;

                if (item.ItemType == LibraryItemType.Lua)
                {
                    // Case A: lua file exists but the game isn't actually installed in
                    // Steam. There's nothing to "uninstall" — just delete the lua.
                    if (item.IsLuaUninstalled)
                    {
                        var confirm = MessageBoxHelper.Show(
                            $"Delete the lua file for {item.Name}?\n\nThe game isn't installed in Steam, so this just removes the .lua file from stplug-in.",
                            "Delete Lua",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);
                        if (confirm != MessageBoxResult.Yes) return;

                        success = await Task.Run(() => _fileInstallService.UninstallGame(
                            item.AppId, uninstallFromSteam: false, deleteLua: true));
                    }
                    else
                    {
                        // Case B: lua-backed game IS installed in Steam. Offer to keep the
                        // lua around so the user doesn't lose the config if they just want
                        // to free up disk space by wiping the game files.
                        var prompt = MessageBoxHelper.Show(
                            $"Uninstall {item.Name}?\n\n" +
                            "Yes  — Uninstall the game from Steam AND delete the lua file\n" +
                            "No   — Uninstall the game from Steam but KEEP the lua file\n" +
                            "Cancel — Do nothing",
                            "Confirm Uninstall",
                            MessageBoxButton.YesNoCancel,
                            MessageBoxImage.Warning);

                        if (prompt == MessageBoxResult.Cancel) return;

                        var deleteLua = prompt == MessageBoxResult.Yes;
                        removeFromLibrary = deleteLua; // If we keep the lua, the Library row stays.

                        success = await Task.Run(() => _fileInstallService.UninstallGame(
                            item.AppId, uninstallFromSteam: true, deleteLua: deleteLua));
                    }
                }
                else if (item.ItemType == LibraryItemType.SteamGame)
                {
                    var confirm = MessageBoxHelper.Show(
                        $"Are you sure you want to uninstall {item.Name}?\n\nThis will delete the game files and remove it from Steam.",
                        "Confirm Uninstall",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (confirm != MessageBoxResult.Yes) return;

                    success = await Task.Run(() => _steamGamesService.UninstallGame(item.AppId));
                }

                if (success)
                {
                    if (removeFromLibrary)
                    {
                        lock (_allItemsLock)
                        {
                            _allItems.Remove(item);
                        }
                        _dbService.DeleteLibraryItem(item.AppId);
                    }
                    else
                    {
                        // Game is gone from Steam but the lua is still on disk, so the
                        // item stays as "lua-only / not installed".
                        item.IsInstalledOnSteam = false;
                        item.SizeBytes = 0;
                    }

                    // Notify Store (and anything else listening) so the stale IsInstalled
                    // flag on cached game cards gets cleared without a manual reload.
                    try { _refreshService.NotifyGameUninstalled(item.AppId); } catch { }

                    ApplyFilters();

                    lock (_allItemsLock)
                    {
                        TotalLua = _allItems.Count(i => i.ItemType == LibraryItemType.Lua);
                        TotalSteamGames = _allItems.Count(i => i.ItemType == LibraryItemType.SteamGame);
                        TotalSize = _allItems.Sum(i => i.SizeBytes);
                    }

                    _notificationService.ShowSuccess($"{item.Name} uninstalled successfully");
                }
                else
                {
                    _notificationService.ShowError($"Failed to uninstall {item.Name}");
                }
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to uninstall: {ex.Message}");
            }
        }

        /// <summary>
        /// Asks Steam to install a lua-registered game via the steam://install/{appId}
        /// protocol. Only meaningful in SteamTools mode — in DepotDownloader mode the
        /// Install button is hidden in XAML.
        ///
        /// Caveat: Steam only honors steam://install/ if it already "owns" the app, which
        /// means the lua has to be picked up on a Steam restart. If the lua was just added
        /// and Steam hasn't been restarted yet, the protocol falls back to the store page.
        /// We let Steam handle that fallback rather than second-guessing it.
        /// </summary>
        [RelayCommand]
        private Task InstallLuaGame(LibraryItem item)
        {
            if (item == null || item.ItemType != LibraryItemType.Lua) return Task.CompletedTask;

            try
            {
                var url = $"steam://install/{item.AppId}";
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });

                StatusMessage = $"Asked Steam to install {item.Name}...";
                _notificationService.ShowSuccess(
                    $"Asked Steam to install {item.Name}.\n\nIf Steam opens the store page instead of the install dialog, restart Steam so it picks up the lua, then click Install again.");
            }
            catch (Exception ex)
            {
                _logger.Error($"[LibraryViewModel] Failed to launch steam install for {item.AppId}: {ex.Message}");
                _notificationService.ShowError($"Failed to launch Steam install: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Downloads the manifest zip for a foreign-format lua and installs it, overwriting
        /// whatever was there before with a fresh tool-formatted version. Functionally the
        /// same as clicking Download on the Store page — reuses DownloadGameFileOnlyAsync
        /// and FileInstallService.InstallFromZipAsync.
        /// </summary>
        [RelayCommand]
        private async Task RefetchLua(LibraryItem item)
        {
            if (item == null || item.ItemType != LibraryItemType.Lua) return;
            if (!uint.TryParse(item.AppId, out _))
            {
                _notificationService.ShowWarning($"Can't refetch '{item.Name}' — the filename isn't a numeric AppID.");
                return;
            }

            var settings = _settingsService.LoadSettings();
            if (string.IsNullOrEmpty(settings.ApiKey))
            {
                MessageBoxHelper.Show(
                    "Please enter your API key in Settings before refetching lua files.",
                    "API Key Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                StatusMessage = $"Refetching {item.Name}...";

                var manifest = new Manifest
                {
                    AppId = item.AppId,
                    Name = item.Name,
                    IconUrl = item.IconUrl,
                    Size = 0,
                    DownloadUrl = $"{ManifestApiService.BaseUrl}/manifest/{item.AppId}"
                };

                var zipFilePath = await _downloadService.DownloadGameFileOnlyAsync(
                    manifest,
                    settings.DownloadsPath,
                    settings.ApiKey);

                await _fileInstallService.InstallFromZipAsync(
                    zipFilePath,
                    msg => StatusMessage = msg);

                // Clean up the zip if the user has that preference set.
                if (settings.DeleteZipAfterInstall)
                {
                    try { File.Delete(zipFilePath); } catch { }
                }

                // Now re-scan this one lua so the UI flips the flag back off.
                item.IsForeignFormat = false;

                _notificationService.ShowSuccess($"{item.Name} refetched and overwritten with the tool's version.");
                StatusMessage = $"{item.Name} refetched";
            }
            catch (Exception ex)
            {
                _logger.Error($"[LibraryViewModel] Failed to refetch {item.AppId}: {ex.Message}");
                _notificationService.ShowError($"Failed to refetch {item.Name}: {ex.Message}");
            }
        }

        [RelayCommand]
        private void RestartSteam()
        {
            try
            {
                _steamService.RestartSteam();
                StatusMessage = "Steam restarting...";
                _notificationService.ShowSuccess("Steam is restarting...");
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to restart Steam: {ex.Message}");
            }
        }

        [RelayCommand]
        private void ToggleSelectMode()
        {
            IsSelectMode = !IsSelectMode;
            if (!IsSelectMode)
            {
                // Deselect all
                List<LibraryItem> snapshot;
                lock (_allItemsLock)
                {
                    snapshot = _allItems.ToList();
                }
                foreach (var item in snapshot)
                {
                    item.IsSelected = false;
                }
            }
        }

        [RelayCommand]
        private void SelectAll()
        {
            foreach (var item in DisplayedItems)
            {
                item.IsSelected = true;
            }
        }

        [RelayCommand]
        private void DeselectAll()
        {
            foreach (var item in DisplayedItems)
            {
                item.IsSelected = false;
            }
        }

        [RelayCommand]
        private async Task UninstallSelected()
        {
            var selected = DisplayedItems.Where(i => i.IsSelected).ToList();
            if (!selected.Any())
            {
                MessageBoxHelper.Show("No items selected", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var luaCount = selected.Count(i => i.ItemType == LibraryItemType.Lua);
            var gameCount = selected.Count(i => i.ItemType == LibraryItemType.SteamGame);
            var message = luaCount > 0 && gameCount > 0
                ? $"Are you sure you want to uninstall {luaCount} lua file(s) and {gameCount} Steam game(s)?"
                : luaCount > 0
                    ? $"Are you sure you want to uninstall {luaCount} lua file(s)?"
                    : $"Are you sure you want to uninstall {gameCount} Steam game(s)?";

            var result = MessageBoxHelper.Show(
                message,
                "Confirm Batch Uninstall",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                int successCount = 0;
                foreach (var item in selected)
                {
                    try
                    {
                        bool success = false;

                        if (item.ItemType == LibraryItemType.Lua)
                        {
                            success = await Task.Run(() => _fileInstallService.UninstallGame(item.AppId));
                        }
                        else if (item.ItemType == LibraryItemType.SteamGame)
                        {
                            success = await Task.Run(() => _steamGamesService.UninstallGame(item.AppId));
                        }

                        if (success)
                        {
                            lock (_allItemsLock)
                            {
                                _allItems.Remove(item);
                            }
                            _dbService.DeleteLibraryItem(item.AppId);
                            try { _refreshService.NotifyGameUninstalled(item.AppId); } catch { }
                            successCount++;
                        }
                    }
                    catch { }
                }

                ApplyFilters();
                lock (_allItemsLock)
                {
                    TotalLua = _allItems.Count(i => i.ItemType == LibraryItemType.Lua);
                    TotalSteamGames = _allItems.Count(i => i.ItemType == LibraryItemType.SteamGame);
                    TotalSize = _allItems.Sum(i => i.SizeBytes);
                }

                _notificationService.ShowSuccess($"{successCount} item(s) uninstalled successfully");
                IsSelectMode = false;
            }
        }

        /// <summary>Launch the game via Steam's run protocol.</summary>
        [RelayCommand]
        private void RunInSteam(LibraryItem item)
        {
            if (item == null) return;
            try
            {
                _recentGamesService.MarkAsRecentlyAccessed(item.AppId);
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://rungameid/{item.AppId}",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to launch via Steam: {ex.Message}");
            }
        }

        /// <summary>Open the game's Steam store page in the Steam client (or browser).</summary>
        [RelayCommand]
        private void OpenSteamStorePage(LibraryItem item)
        {
            if (item == null) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://store/{item.AppId}",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to open store page: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task FixGame(LibraryItem item)
        {
            if (item == null) return;

            var settings = _settingsService.LoadSettings();

            // Prompt for Steam Web API key if not set
            if (string.IsNullOrEmpty(settings.FixGameSteamWebApiKey))
            {
                var result = MessageBoxHelper.Show(
                    "Fix Game needs a Steam Web API key for achievements and stats.\n\n" +
                    "Get one at: https://steamcommunity.com/dev/apikey\n\n" +
                    "You can set it in Settings > Fix Game tab.\n\nContinue without achievements?",
                    "Steam Web API Key", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;
            }

            // Need the game's install path
            var gameDir = item.LocalPath;
            if (string.IsNullOrEmpty(gameDir) || !System.IO.Directory.Exists(gameDir))
            {
                // Try to find via ManifestStorageService or ask user
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = $"Select game folder for {item.Name}"
                };
                if (dialog.ShowDialog() != true) return;
                gameDir = dialog.FolderName;
            }
            else
            {
                // LocalPath might be the lua file, not the game dir
                if (System.IO.File.Exists(gameDir))
                    gameDir = System.IO.Path.GetDirectoryName(gameDir) ?? gameDir;
            }

            // Check DRM before running Fix Game
            try
            {
                using var drmClient = new System.Net.Http.HttpClient();
                drmClient.Timeout = TimeSpan.FromSeconds(10);
                var storeJson = await drmClient.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={item.AppId}");
                var storeData = Newtonsoft.Json.Linq.JObject.Parse(storeJson);
                var gameData = storeData[item.AppId]?["data"];
                var drmNotice = gameData?["drm_notice"]?.ToString() ?? "";
                var extAccount = gameData?["ext_user_account_notice"]?.ToString() ?? "";

                if (!string.IsNullOrEmpty(extAccount))
                {
                    MessageBoxHelper.Show(
                        $"This game requires: {extAccount}\n\nFix Game cannot bypass third-party account requirements.",
                        "Cannot Fix Game", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (System.Text.RegularExpressions.Regex.IsMatch(drmNotice, @"(?i)denuvo"))
                {
                    MessageBoxHelper.Show(
                        $"DRM: {drmNotice}\n\nFix Game cannot bypass this protection.",
                        "Cannot Fix Game", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            catch { /* Can't check, proceed anyway */ }

            StatusMessage = $"Fixing {item.Name}...";
            _notificationService.ShowSuccess($"Applying emulator to {item.Name}...");

            var success = await _fixGameService.FixGameAsync(
                item.AppId,
                gameDir,
                settings.FixGameSteamWebApiKey,
                settings.FixGameLanguage,
                settings.FixGameSteamId,
                settings.FixGamePlayerName,
                settings.FixGameMode);

            StatusMessage = success
                ? $"Emulator applied to {item.Name}"
                : $"Emulator failed for {item.Name}";

            if (success)
                _notificationService.ShowSuccess($"Emulator applied to {item.Name}!");
            else
                MessageBoxHelper.Show(
                    $"Fix Game failed for {item.Name}.\n\nCheck the log for details.",
                    "Fix Game Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>Copy the item's AppID to the clipboard.</summary>
        [RelayCommand]
        private void CopyAppId(LibraryItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.AppId)) return;
            try
            {
                System.Windows.Clipboard.SetText(item.AppId);
                _notificationService.ShowSuccess($"Copied AppID {item.AppId} to clipboard");
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to copy: {ex.Message}");
            }
        }

        [RelayCommand]
        private void OpenInExplorer(LibraryItem item)
        {
            // Track as recently accessed
            _recentGamesService.MarkAsRecentlyAccessed(item.AppId);

            try
            {
                string? pathToOpen = null;

                // Try to find the path based on item type
                if (!string.IsNullOrEmpty(item.LocalPath) && (File.Exists(item.LocalPath) || Directory.Exists(item.LocalPath)))
                {
                    pathToOpen = item.LocalPath;
                }
                else if (item.ItemType == LibraryItemType.Lua)
                {
                    // Try to find the .lua file
                    var stpluginPath = _steamService.GetStPluginPath();
                    if (!string.IsNullOrEmpty(stpluginPath))
                    {
                        var luaFile = Path.Combine(stpluginPath, $"{item.AppId}.lua");
                        if (File.Exists(luaFile))
                        {
                            pathToOpen = luaFile;
                        }
                        else
                        {
                            var luaFileDisabled = Path.Combine(stpluginPath, $"{item.AppId}.lua.disabled");
                            if (File.Exists(luaFileDisabled))
                            {
                                pathToOpen = luaFileDisabled;
                            }
                        }
                    }
                }
                else if (item.ItemType == LibraryItemType.SteamGame)
                {
                    // Try to find the Steam game folder
                    var steamGames = _steamGamesService.GetInstalledGames();
                    var steamGame = steamGames.FirstOrDefault(g => g.AppId == item.AppId);
                    if (steamGame != null && !string.IsNullOrEmpty(steamGame.LibraryPath) && Directory.Exists(steamGame.LibraryPath))
                    {
                        pathToOpen = steamGame.LibraryPath;
                    }
                }

                if (!string.IsNullOrEmpty(pathToOpen))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = File.Exists(pathToOpen) ? $"/select,\"{pathToOpen}\"" : $"\"{pathToOpen}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    _notificationService.ShowWarning($"Could not find local path for {item.Name}");
                }
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to open explorer: {ex.Message}");
            }
        }

        [RelayCommand]
        private void ShowDetails(LibraryItem item)
        {
            try
            {
                // This will open a details window - to be implemented
                var details = $"Name: {item.Name}\n" +
                             $"App ID: {item.AppId}\n" +
                             $"Type: {item.TypeBadge}\n" +
                             $"Size: {item.SizeFormatted}\n" +
                             $"Install Date: {item.InstallDate?.ToString("yyyy-MM-dd HH:mm") ?? "Unknown"}\n" +
                             $"Last Updated: {item.LastUpdated?.ToString("yyyy-MM-dd HH:mm") ?? "Unknown"}\n" +
                             $"Path: {(string.IsNullOrEmpty(item.LocalPath) ? "Not available" : item.LocalPath)}";

                MessageBoxHelper.Show(details, $"Details: {item.Name}", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to show details: {ex.Message}");
            }
        }
    }
}
