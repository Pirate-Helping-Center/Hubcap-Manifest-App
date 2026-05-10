using HubcapManifestApp.Helpers;
using HubcapManifestApp.Models;
using HubcapManifestApp.Services;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace HubcapManifestApp.ViewModels
{
    public partial class StoreViewModel
    {
        [RelayCommand]
        private async Task DownloadSelected()
        {
            try
            {
            var selected = Games.Where(g => g.IsSelected && g.ManifestAvailable).ToList();
            if (selected.Count == 0)
            {
                MessageBoxHelper.Show("No downloadable games selected", "Info",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var settings = _settingsService.LoadSettings();
            if (string.IsNullOrEmpty(settings.ApiKey))
            {
                MessageBoxHelper.Show("Please enter API key in settings", "API Key Required",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Check API quota
            var stats = await _manifestApiService.GetUserStatsAsync(settings.ApiKey);
            if (stats != null)
            {
                if (!stats.CanMakeRequests)
                {
                    MessageBoxHelper.Show("You've reached your daily API limit. Try again tomorrow.",
                        "Limit Reached", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (selected.Count > stats.Remaining)
                {
                    var result = MessageBoxHelper.Show(
                        $"You have {stats.Remaining} downloads remaining today ({stats.DailyUsage}/{stats.DailyLimit} used).\n\n" +
                        $"You selected {selected.Count} games — some downloads may fail if you exceed your limit.\n\nContinue?",
                        "API Limit Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result != MessageBoxResult.Yes) return;
                }
            }

            var confirm = MessageBoxHelper.Show(
                $"Download {selected.Count} game(s)?",
                "Confirm Bulk Download", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            int queued = 0;
            StatusMessage = $"Queueing {selected.Count} game(s)...";
            foreach (var game in selected)
            {
                try
                {
                    var manifest = new Manifest
                    {
                        AppId = game.GameId,
                        Name = game.GameName,
                        IconUrl = game.HeaderImage,
                        Size = game.ManifestSize ?? 0,
                        DownloadUrl = $"{ManifestApiService.BaseUrl}/manifest/{game.GameId}"
                    };
                    _downloadService.AddToQueue(manifest, settings.DownloadsPath, settings.ApiKey,
                        _steamService.GetSteamPath() ?? "");
                    queued++;
                    StatusMessage = $"Queued {queued}/{selected.Count}...";
                    if (queued < selected.Count)
                        await System.Threading.Tasks.Task.Delay(1000);
                }
                catch { }
            }

            _notificationService.ShowSuccess($"{queued} game(s) queued for download");

            IsSelectMode = false;
            foreach (var game in Games) game.IsSelected = false;
            SelectedCount = 0;
            StatusMessage = $"{queued} game(s) queued for download";
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Bulk download failed: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task DownloadGame(LibraryGame game)
        {
            var settings = _settingsService.LoadSettings();

            if (string.IsNullOrEmpty(settings.ApiKey))
            {
                MessageBoxHelper.Show(
                    "Please enter API key in settings",
                    "API Key Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!game.ManifestAvailable)
            {
                MessageBoxHelper.Show(
                    $"Manifest for '{game.GameName}' is not available yet.",
                    "Not Available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            try
            {
                // Check for DRM / external account requirements
                try
                {
                    using var drmClient = new System.Net.Http.HttpClient();
                    drmClient.Timeout = TimeSpan.FromSeconds(10);
                    var storeJson = await drmClient.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={game.GameId}");
                    var storeData = Newtonsoft.Json.Linq.JObject.Parse(storeJson);
                    var gameData = storeData[game.GameId]?["data"];
                    var drmNotice = gameData?["drm_notice"]?.ToString() ?? "";
                    var extAccount = gameData?["ext_user_account_notice"]?.ToString() ?? "";

                    string warning = "";
                    bool isDenuvo = false;

                    if (!string.IsNullOrEmpty(drmNotice))
                    {
                        isDenuvo = System.Text.RegularExpressions.Regex.IsMatch(drmNotice, @"(?i)denuvo");
                        warning += $"DRM: {drmNotice}\n";
                    }
                    if (!string.IsNullOrEmpty(extAccount))
                        warning += $"Requires: {extAccount}\n";

                    if (!string.IsNullOrEmpty(warning))
                    {
                        if (isDenuvo || !string.IsNullOrEmpty(extAccount))
                        {
                            warning += "\nThis game cannot be fixed automatically.";
                            if (settings.FixGameAutoAfterDownload)
                                warning += " Auto-emu will be skipped.";
                        }
                        else if (!string.IsNullOrEmpty(drmNotice))
                        {
                            warning += "\nColdClient mode will be used automatically to handle this DRM.";
                        }
                        warning += "\n\nDo you still want to download?";
                        var drmResult = MessageBoxHelper.Show(warning, "DRM Warning",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (drmResult != MessageBoxResult.Yes)
                        {
                            StatusMessage = "Download cancelled";
                            return;
                        }
                    }
                }
                catch { /* Can't check DRM, proceed anyway */ }

                // Create a manifest object for download
                var manifest = new Manifest
                {
                    AppId = game.GameId,
                    Name = game.GameName,
                    IconUrl = game.HeaderImage,
                    Size = game.ManifestSize ?? 0,
                    DownloadUrl = $"{ManifestApiService.BaseUrl}/manifest/{game.GameId}"
                };

                StatusMessage = $"Downloading: {game.GameName}";
                var zipFilePath = await _downloadService.DownloadGameFileOnlyAsync(manifest, settings.DownloadsPath, settings.ApiKey);

                StatusMessage = $"{game.GameName} downloaded successfully";

                if (!settings.AutoInstallAfterDownload)
                {
                    MessageBoxHelper.Show(
                        $"{game.GameName} has been downloaded!\n\nGo to the Downloads page to install it.",
                        "Download Complete",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (System.Exception ex)
            {
                StatusMessage = $"Download failed: {ex.Message}";
                MessageBoxHelper.Show(
                    $"Failed to download {game.GameName}: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private async Task UpdateGame(LibraryGame game)
        {
            var settings = _settingsService.LoadSettings();

            if (string.IsNullOrEmpty(settings.ApiKey))
            {
                MessageBoxHelper.Show(
                    "Please enter API key in settings",
                    "API Key Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!game.ManifestAvailable)
            {
                MessageBoxHelper.Show(
                    $"Manifest for '{game.GameName}' is not available yet.",
                    "Not Available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // Resolve install info via cache → marker scan → folder picker fallback.
            var scanRoots = new List<string>();
            if (!string.IsNullOrEmpty(settings.DepotDownloaderOutputPath))
                scanRoots.Add(settings.DepotDownloaderOutputPath);
            var installedInfo = _manifestStorageService.GetInstalledManifestWithFallback(game.GameId, scanRoots);

            if (installedInfo == null)
            {
                // Three options: locate existing, fresh install, cancel.
                var prompt = MessageBoxHelper.Show(
                    $"No installation info found for '{game.GameName}'.\n\n" +
                    "Yes — locate the existing install folder (verify & patch)\n" +
                    "No — download a fresh copy instead\n" +
                    "Cancel — do nothing",
                    "Update " + game.GameName,
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (prompt == MessageBoxResult.Cancel)
                    return;

                if (prompt == MessageBoxResult.No)
                {
                    // Fresh install fallback — kick off the normal Download flow and bail out of Update.
                    await DownloadGame(game);
                    return;
                }

                var picker = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = $"Locate {game.GameName} install folder"
                };
                if (picker.ShowDialog() != true)
                {
                    // User cancelled the picker — offer fresh install as a final fallback.
                    var fallback = MessageBoxHelper.Show(
                        $"No folder selected. Download {game.GameName} as a fresh install instead?",
                        "Update " + game.GameName,
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (fallback == MessageBoxResult.Yes)
                        await DownloadGame(game);
                    return;
                }

                var chosenPath = picker.FolderName;
                if (!Directory.Exists(chosenPath))
                {
                    MessageBoxHelper.Show("Selected folder does not exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Check if the chosen folder already has a marker (e.g. user copied install from another machine)
                var existingMarker = _manifestStorageService.TryReadInstallMarker(chosenPath);
                if (existingMarker != null && existingMarker.AppId == game.GameId)
                {
                    installedInfo = existingMarker;
                    // Refresh cache so future updates are one-click
                    _manifestStorageService.StoreManifest(
                        existingMarker.AppId, existingMarker.GameName,
                        existingMarker.ManifestId, existingMarker.InstallPath, existingMarker.DepotIds);
                }
                else
                {
                    // Create a stub entry; ManifestId/DepotIds get filled in after the verify-update completes.
                    installedInfo = new InstalledManifestInfo
                    {
                        AppId = game.GameId,
                        GameName = game.GameName,
                        ManifestId = 0,
                        InstalledDate = DateTime.Now,
                        InstallPath = chosenPath,
                        DepotIds = new List<uint>()
                    };
                    _manifestStorageService.StoreManifest(
                        installedInfo.AppId, installedInfo.GameName,
                        installedInfo.ManifestId, installedInfo.InstallPath, installedInfo.DepotIds);
                }
            }

            try
            {
                var manifest = new Manifest
                {
                    AppId = game.GameId,
                    Name = game.GameName,
                    IconUrl = game.HeaderImage,
                    Size = game.ManifestSize ?? 0,
                    DownloadUrl = $"{ManifestApiService.BaseUrl}/manifest/{game.GameId}"
                };

                StatusMessage = $"Downloading update manifest for {game.GameName}...";
                var zipFilePath = await _downloadService.DownloadGameFileOnlyAsync(manifest, settings.DownloadsPath, settings.ApiKey);

                // Extract lua + manifests, build depot list, then trigger DepotDownloader in
                // verify-and-patch mode against the existing install directory.
                var luaContent = _downloadService.ExtractLuaContentFromZip(zipFilePath, game.GameId);
                var manifestFiles = _downloadService.ExtractManifestFilesFromZip(zipFilePath, game.GameId);
                var depotFilterService = _depotFilterService;
                var parsedDepotKeys = depotFilterService.ExtractDepotKeysFromLua(luaContent);

                // Filter depots: prefer the previously-installed depot set if we know it.
                // Otherwise use everything in the manifest zip.
                IEnumerable<string> depotIdsToUse;
                if (installedInfo.DepotIds != null && installedInfo.DepotIds.Count > 0)
                {
                    var installedSet = new HashSet<string>(installedInfo.DepotIds.Select(d => d.ToString()));
                    depotIdsToUse = parsedDepotKeys.Keys.Where(k => installedSet.Contains(k));
                }
                else
                {
                    depotIdsToUse = parsedDepotKeys.Keys;
                }

                var depotsToDownload = new List<(uint depotId, string depotKey, string? manifestFile, uint ownerAppId)>();
                foreach (var depotIdStr in depotIdsToUse)
                {
                    if (!uint.TryParse(depotIdStr, out var depotId)) continue;
                    if (!parsedDepotKeys.TryGetValue(depotIdStr, out var depotKey)) continue;
                    string? manifestFilePath = manifestFiles.TryGetValue(depotIdStr, out var mp) ? mp : null;

                    // Owner defaults to main appId; the wrapper's PICS-first owner resolution
                    // will override with dlcappid/depotfromapp data on the live data path.
                    if (uint.TryParse(game.GameId, out var ownerAppId))
                    {
                        depotsToDownload.Add((depotId, depotKey, manifestFilePath, ownerAppId));
                    }
                }

                if (depotsToDownload.Count == 0)
                {
                    MessageBoxHelper.Show(
                        $"No depots found in the manifest zip for {game.GameName}.",
                        "Update Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                ulong primaryManifestId = 0;
                try { primaryManifestId = _luaParser.GetPrimaryManifestId(luaContent, game.GameId); } catch { }

                StatusMessage = $"Verifying and patching {game.GameName} ({depotsToDownload.Count} depots)...";

                // Fire the verify-and-patch download against the existing install directory.
                // useRawTargetDirectory=true makes the wrapper write directly into installedInfo.InstallPath
                // instead of nesting under {GameName} ({AppId})/{GameName}/.
                _ = _downloadService.DownloadViaDepotDownloaderAsync(
                    game.GameId,
                    game.GameName,
                    depotsToDownload,
                    installedInfo.InstallPath,
                    verifyFiles: true,
                    maxDownloads: settings.MaxConcurrentDownloads,
                    primaryManifestId: primaryManifestId,
                    useRawTargetDirectory: true
                );

                StatusMessage = $"{game.GameName} update started — check Downloads tab";
                _notificationService.ShowSuccess(
                    $"Verifying and patching {game.GameName}.\n\nDelta download — only changed chunks will be transferred.\n\nTarget: {installedInfo.InstallPath}",
                    "Update Started"
                );

                if (settings.DeleteZipAfterInstall)
                {
                    try { File.Delete(zipFilePath); } catch { }
                }

                game.HasUpdate = false;
            }
            catch (System.Exception ex)
            {
                StatusMessage = $"Update download failed: {ex.Message}";
                MessageBoxHelper.Show(
                    $"Failed to download update for {game.GameName}: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
