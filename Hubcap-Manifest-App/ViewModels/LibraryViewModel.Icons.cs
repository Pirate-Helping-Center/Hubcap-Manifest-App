using HubcapManifestApp.Helpers;
using HubcapManifestApp.Models;
using HubcapManifestApp.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;

namespace HubcapManifestApp.ViewModels
{
    // Steam app cache updates, lua export, and image caching.
    public partial class LibraryViewModel
    {
        [RelayCommand]
        private async Task UpdateSteamAppCache()
        {
            try
            {
                var settings = _settingsService.LoadSettings();

                IsLoading = true;
                StatusMessage = "Updating Steam app list cache...";

                _logger.Info("Starting Steam app list cache update");

                // Force refresh the app list from API
                var result = await _steamApiService.GetAppListAsync(forceRefresh: true);

                if (result != null && result.AppList?.Apps.Count > 0)
                {
                    _logger.Info($"Successfully updated cache with {result.AppList.Apps.Count} apps");
                    _notificationService.ShowSuccess($"Steam app list cache updated! Loaded {result.AppList.Apps.Count:N0} apps.");
                }
                else
                {
                    _logger.Warning("Cache update returned empty result");
                    _notificationService.ShowWarning("Cache update completed but no apps were retrieved. Check your API key.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to update Steam app cache: {ex.Message}");
                _notificationService.ShowError($"Failed to update cache: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
                StatusMessage = $"{_allItems.Count} item(s) loaded";
            }
        }

        [RelayCommand]
        private async Task ExportLuas()
        {
            try
            {
                var stpluginPath = _steamService.GetStPluginPath();
                if (string.IsNullOrEmpty(stpluginPath) || !Directory.Exists(stpluginPath))
                {
                    _notificationService.ShowError("Could not find stplug-in directory.");
                    return;
                }

                var luaFiles = Directory.GetFiles(stpluginPath, "*.lua");
                if (luaFiles.Length == 0)
                {
                    _notificationService.ShowWarning("No .lua files found to export.");
                    return;
                }

                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export Lua Files",
                    Filter = "ZIP Archive (*.zip)|*.zip",
                    FileName = $"HubcapLuaExport_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
                };

                if (dialog.ShowDialog() != true)
                    return;

                IsLoading = true;
                StatusMessage = $"Exporting {luaFiles.Length} lua files...";

                await Task.Run(() =>
                {
                    if (File.Exists(dialog.FileName))
                        File.Delete(dialog.FileName);

                    using var zip = ZipFile.Open(dialog.FileName, ZipArchiveMode.Create);
                    foreach (var file in luaFiles)
                    {
                        zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
                    }
                });

                _notificationService.ShowSuccess($"Exported {luaFiles.Length} lua file(s) to {Path.GetFileName(dialog.FileName)}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to export luas: {ex.Message}");
                _notificationService.ShowError($"Export failed: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
                StatusMessage = $"{_allItems.Count} item(s) loaded";
            }
        }

        /// <summary>
        /// Caches BitmapImages in memory for all library items with available icon paths.
        /// Runs asynchronously in background to improve Library page loading performance.
        /// </summary>
        private async Task CacheImagesAsync()
        {
            try
            {
                List<LibraryItem> snapshot;
                lock (_allItemsLock)
                {
                    snapshot = _allItems.ToList();
                }

                _logger.Info($"Starting image caching for {snapshot.Count} library items...");

                var imagesToCache = new Dictionary<string, string>();

                foreach (var item in snapshot)
                {
                    if (!string.IsNullOrEmpty(item.CachedIconPath) && File.Exists(item.CachedIconPath))
                    {
                        imagesToCache[item.AppId] = item.CachedIconPath;
                    }
                }

                _logger.Info($"Found {imagesToCache.Count} images to cache");

                // Pre-load all images into cache
                await _imageCacheService.PreloadImagesAsync(imagesToCache);

                // Update LibraryItem.CachedBitmapImage properties on UI thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var item in snapshot)
                    {
                        if (imagesToCache.ContainsKey(item.AppId))
                        {
                            // Get cached image
                            var bitmap = _imageCacheService.GetCachedImage(item.AppId);
                            if (bitmap != null)
                            {
                                item.CachedBitmapImage = bitmap;
                            }
                        }
                    }

                    _logger.Info($"✓ Image caching complete! Cache size: {_imageCacheService.GetCacheSize()} images ({_imageCacheService.GetEstimatedMemoryUsageMB():F1} MB)");
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Error caching images: {ex.Message}");
            }
        }
    }
}
