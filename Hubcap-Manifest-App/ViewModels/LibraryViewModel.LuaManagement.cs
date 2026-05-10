using HubcapManifestApp.Helpers;
using HubcapManifestApp.Models;
using HubcapManifestApp.Services;
using HubcapManifestApp.Views.Dialogs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;

namespace HubcapManifestApp.ViewModels
{
    // Lua-specific operations: patch, enable/disable, delete, drag-drop, auto-updates.
    public partial class LibraryViewModel
    {
        [RelayCommand]
        private async Task PatchAll()
        {
            try
            {
                var result = MessageBoxHelper.Show(
                    "This will patch all .lua files by commenting out setManifestid lines.\n\nContinue?",
                    "Patch All .lua Files",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                    return;

                IsLoading = true;
                StatusMessage = "Patching .lua files...";

                var (luaFiles, _) = _luaFileManager.FindLuaFiles();
                int patchedCount = 0;

                foreach (var luaFile in luaFiles)
                {
                    var patchResult = _luaFileManager.PatchLuaFile(luaFile);
                    if (patchResult == "patched")
                    {
                        patchedCount++;
                    }
                }

                _notificationService.ShowSuccess($"Patched {patchedCount} file(s). Restart Steam for changes to take effect.");
                await RefreshLibrary();
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to patch files: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private async Task EnableGame(string appId)
        {
            try
            {
                var (success, message) = _luaFileManager.EnableGame(appId);
                if (success)
                {
                    _notificationService.ShowSuccess(message);
                    await RefreshLibrary();
                }
                else
                {
                    _notificationService.ShowError(message);
                }
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to enable game: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task DisableGame(string appId)
        {
            try
            {
                var (success, message) = _luaFileManager.DisableGame(appId);
                if (success)
                {
                    _notificationService.ShowSuccess(message);
                    await RefreshLibrary();
                }
                else
                {
                    _notificationService.ShowError(message);
                }
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to disable game: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task DeleteLua(string appId)
        {
            try
            {
                var result = MessageBoxHelper.Show(
                    $"Are you sure you want to permanently delete the .lua file for App ID {appId}?\n\nThis cannot be undone!",
                    "Confirm Delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return;

                var (success, message) = _luaFileManager.DeleteLuaFile(appId);
                if (success)
                {
                    _notificationService.ShowSuccess(message);
                    await RefreshLibrary();
                }
                else
                {
                    _notificationService.ShowError(message);
                }
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to delete lua file: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task ProcessDroppedFiles(string[] filePaths)
        {
            try
            {
                var luaFiles = new List<string>();
                var tempDirs = new List<string>();

                foreach (var filePath in filePaths)
                {
                    if (filePath.ToLower().EndsWith(".lua"))
                    {
                        if (ArchiveExtractionService.IsValidLuaFilename(Path.GetFileName(filePath)))
                        {
                            luaFiles.Add(filePath);
                        }
                    }
                    else if (filePath.ToLower().EndsWith(".zip"))
                    {
                        var (archiveLuaFiles, tempDir) = _archiveExtractor.ExtractLuaFromArchive(filePath);
                        luaFiles.AddRange(archiveLuaFiles);
                        if (!string.IsNullOrEmpty(tempDir))
                        {
                            tempDirs.Add(tempDir);
                        }
                    }
                }

                if (luaFiles.Count == 0)
                {
                    _notificationService.ShowWarning("No valid .lua files found");
                    return;
                }

                // Copy files to stplug-in directory
                var stpluginPath = _steamService.GetStPluginPath();
                if (string.IsNullOrEmpty(stpluginPath))
                {
                    _notificationService.ShowError("Could not find Steam stplug-in directory");
                    return;
                }

                int copiedCount = 0;
                var installedAppIds = new List<string>();

                foreach (var luaFile in luaFiles)
                {
                    var fileName = Path.GetFileName(luaFile);
                    var destPath = Path.Combine(stpluginPath, fileName);

                    // Extract appId from filename (e.g., "123456.lua" -> "123456")
                    var appId = Path.GetFileNameWithoutExtension(fileName);

                    // Remove existing files
                    if (File.Exists(destPath))
                        File.Delete(destPath);
                    if (File.Exists(destPath + ".disabled"))
                        File.Delete(destPath + ".disabled");

                    File.Copy(luaFile, destPath, true);
                    copiedCount++;
                    installedAppIds.Add(appId);
                }

                // Cleanup temp directories
                foreach (var tempDir in tempDirs)
                {
                    _archiveExtractor.CleanupTempDirectory(tempDir);
                }

                // Only show notification if user hasn't disabled it
                var settings = _settingsService.LoadSettings();
                if (settings.ShowGameAddedNotification)
                {
                    _notificationService.ShowSuccess($"Successfully added {copiedCount} file(s)! Restart Steam for changes to take effect.");
                }

                // Add games to library instantly instead of full refresh
                foreach (var appId in installedAppIds)
                {
                    await AddGameToLibraryAsync(appId);
                }
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to process files: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task ToggleAutoUpdates()
        {
            var result = MessageBoxHelper.Show(
                "Would you like to enable or disable auto-updates?\n\nYes = Enable\nNo = Disable",
                "Toggle Auto-Updates",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
                await EnableAutoUpdates();
            else if (result == MessageBoxResult.No)
                await DisableAutoUpdates();
        }

        private async Task EnableAutoUpdates()
        {
            // Check if in SteamTools mode
            var settings = _settingsService.LoadSettings();
            if (settings.Mode != ToolMode.SteamTools)
            {
                _notificationService.ShowWarning("Auto-Update Enabler is only available in SteamTools mode");
                return;
            }

            try
            {
                // Get all .lua files
                var (luaFiles, _) = _luaFileManager.FindLuaFiles();
                if (luaFiles.Count == 0)
                {
                    _notificationService.ShowWarning("No .lua files found");
                    return;
                }

                // Build list of apps that currently have updates disabled
                var selectableApps = new List<SelectableApp>();
                foreach (var luaFile in luaFiles)
                {
                    var appId = _luaFileManager.ExtractAppId(luaFile);
                    bool isEnabled = _luaFileManager.IsAutoUpdatesEnabled(appId);

                    // Only show apps that have updates disabled
                    if (!isEnabled)
                    {
                        var nameRaw = await _steamApiService.GetGameNameAsync(appId);
                        var name = (string.IsNullOrWhiteSpace(nameRaw) || nameRaw == AppConstants.UnknownGame) ? $"App {appId}" : nameRaw;

                        selectableApps.Add(new SelectableApp
                        {
                            AppId = appId,
                            Name = name,
                            IsSelected = false,
                            IsUpdateEnabled = isEnabled
                        });
                    }
                }

                if (selectableApps.Count == 0)
                {
                    _notificationService.ShowWarning("All games already have auto-updates enabled");
                    return;
                }

                // Show dialog
                var dialog = new UpdateEnablerDialog(selectableApps);
                var result = dialog.ShowDialog();

                if (result == true && dialog.SelectedApps.Count > 0)
                {
                    // Enable updates for selected apps
                    int successCount = 0;
                    int failCount = 0;

                    foreach (var app in dialog.SelectedApps)
                    {
                        var (success, _) = _luaFileManager.EnableAutoUpdatesForApp(app.AppId);
                        if (success)
                            successCount++;
                        else
                            failCount++;
                    }

                    if (failCount == 0)
                    {
                        _notificationService.ShowSuccess($"Successfully enabled auto-updates for {successCount} app(s)");
                    }
                    else
                    {
                        _notificationService.ShowWarning($"Enabled auto-updates for {successCount} app(s), {failCount} failed");
                    }
                }
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to enable auto-updates: {ex.Message}");
            }
        }

        private async Task DisableAutoUpdates()
        {
            // Check if in SteamTools mode
            var settings = _settingsService.LoadSettings();
            if (settings.Mode != ToolMode.SteamTools)
            {
                _notificationService.ShowWarning("Auto-Update Disabler is only available in SteamTools mode");
                return;
            }

            try
            {
                // Get all .lua files
                var (luaFiles, _) = _luaFileManager.FindLuaFiles();
                if (luaFiles.Count == 0)
                {
                    _notificationService.ShowWarning("No .lua files found");
                    return;
                }

                // Build list of apps that currently have updates enabled
                var selectableApps = new List<SelectableApp>();
                foreach (var luaFile in luaFiles)
                {
                    var appId = _luaFileManager.ExtractAppId(luaFile);
                    bool isEnabled = _luaFileManager.IsAutoUpdatesEnabled(appId);

                    // Only show apps that have updates enabled
                    if (isEnabled)
                    {
                        var nameRaw = await _steamApiService.GetGameNameAsync(appId);
                        var name = (string.IsNullOrWhiteSpace(nameRaw) || nameRaw == AppConstants.UnknownGame) ? $"App {appId}" : nameRaw;

                        selectableApps.Add(new SelectableApp
                        {
                            AppId = appId,
                            Name = name,
                            IsSelected = false,
                            IsUpdateEnabled = isEnabled
                        });
                    }
                }

                if (selectableApps.Count == 0)
                {
                    _notificationService.ShowWarning("All games already have auto-updates disabled");
                    return;
                }

                // Show dialog
                var dialog = new UpdateDisablerDialog(selectableApps);
                var result = dialog.ShowDialog();

                if (result == true && dialog.SelectedApps.Count > 0)
                {
                    // Disable updates for selected apps
                    int successCount = 0;
                    int failCount = 0;

                    foreach (var app in dialog.SelectedApps)
                    {
                        var (success, _) = _luaFileManager.DisableAutoUpdatesForApp(app.AppId);
                        if (success)
                            successCount++;
                        else
                            failCount++;
                    }

                    if (failCount == 0)
                    {
                        _notificationService.ShowSuccess($"Successfully disabled auto-updates for {successCount} app(s)");
                    }
                    else
                    {
                        _notificationService.ShowWarning($"Disabled auto-updates for {successCount} app(s), {failCount} failed");
                    }
                }
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to disable auto-updates: {ex.Message}");
            }
        }
    }
}
