using HubcapManifestApp.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HubcapManifestApp.Models;
using HubcapManifestApp.Services;
using HubcapManifestApp.Views.Dialogs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace HubcapManifestApp.ViewModels
{
    public partial class DownloadsViewModel : ObservableObject, IDisposable
    {
        private bool _disposed;
        private readonly DownloadService _downloadService;
        private readonly FileInstallService _fileInstallService;
        private readonly SettingsService _settingsService;
        private readonly NotificationService _notificationService;
        private readonly LibraryRefreshService _libraryRefreshService;
        private readonly ManifestStorageService _manifestStorageService;
        private readonly DownloadHistoryService _downloadHistoryService;
        private readonly HubcapManifestApp.Services.FixGame.FixGameService _fixGameService;
        private readonly LoggerService _logger;
        private readonly DepotInstallOrchestrator _depotInstallOrchestrator;

        [ObservableProperty]
        private ObservableCollection<DownloadItem> _activeDownloads;

        [ObservableProperty]
        private ObservableCollection<string> _downloadedFiles = new();

        [ObservableProperty]
        private ObservableCollection<DownloadHistoryEntry> _downloadHistory = new();

        [ObservableProperty]
        private string _statusMessage = "No downloads";

        [ObservableProperty]
        private bool _isInstalling;

        public DownloadsViewModel(
            DownloadService downloadService,
            FileInstallService fileInstallService,
            SettingsService settingsService,
            NotificationService notificationService,
            LibraryRefreshService libraryRefreshService,
            ManifestStorageService manifestStorageService,
            DownloadHistoryService downloadHistoryService,
            LoggerService logger,
            DepotInstallOrchestrator depotInstallOrchestrator,
            HubcapManifestApp.Services.FixGame.FixGameService fixGameService)
        {
            _fixGameService = fixGameService;
            _downloadService = downloadService;
            _fileInstallService = fileInstallService;
            _settingsService = settingsService;
            _notificationService = notificationService;
            _libraryRefreshService = libraryRefreshService;
            _manifestStorageService = manifestStorageService;
            _downloadHistoryService = downloadHistoryService;
            _logger = logger;
            _depotInstallOrchestrator = depotInstallOrchestrator;

            ActiveDownloads = _downloadService.ActiveDownloads;

            _ = RefreshDownloadedFiles();
            RefreshHistory();

            _downloadService.DownloadCompleted += OnDownloadCompleted;
            _downloadService.DownloadFailed += OnDownloadFailed;
        }

        private void OnDownloadFailed(object? sender, DownloadItem item)
        {
            App.Current?.Dispatcher.Invoke(RefreshHistory);
        }

        private async void OnDownloadCompleted(object? sender, DownloadItem downloadItem)
        {
            try
            {
            // Auto-refresh the downloaded files list and history when a download completes
            await RefreshDownloadedFiles();
            App.Current?.Dispatcher.Invoke(RefreshHistory);

            // Skip auto-install for DepotDownloader mode (files are downloaded directly, not as zip)
            if (downloadItem.IsDepotDownloaderMode)
            {
                // Backfill manifest storage so the Update flow can find this install later.
                // This writes both the per-install marker (.hubcapmanifestapp/install.json inside
                // the game folder — legacy pre-rebrand installs used .solusmanifestapp/, which the
                // reader still accepts) and updates the AppData cache via StoreManifest().
                try
                {
                    if (!string.IsNullOrEmpty(downloadItem.DestinationPath))
                    {
                        _manifestStorageService.StoreManifest(
                            downloadItem.AppId,
                            downloadItem.GameName,
                            downloadItem.InstallManifestId,
                            downloadItem.DestinationPath,
                            downloadItem.InstallDepotIds);
                        _logger.Info($"Recorded install for {downloadItem.GameName} (AppId: {downloadItem.AppId}) at {downloadItem.DestinationPath}");
                    }
                }
                catch (System.Exception ex)
                {
                    _logger.Error($"Failed to record install for {downloadItem.AppId}: {ex.Message}");
                }

                // Auto-emu if enabled
                var fixSettings = _settingsService.LoadSettings();
                if (fixSettings.FixGameAutoAfterDownload && !string.IsNullOrEmpty(downloadItem.DestinationPath))
                {
                    _logger.Info($"[AutoEmu] Running Fix Game for {downloadItem.GameName}...");
                    _notificationService.ShowSuccess($"Applying emulator to {downloadItem.GameName}...");
                    try
                    {
                        await _fixGameService.FixGameAsync(
                            downloadItem.AppId,
                            downloadItem.DestinationPath,
                            fixSettings.FixGameSteamWebApiKey,
                            fixSettings.FixGameLanguage,
                            fixSettings.FixGameSteamId,
                            fixSettings.FixGamePlayerName,
                            fixSettings.FixGameMode);
                        _notificationService.ShowSuccess($"Emulator applied to {downloadItem.GameName}!");
                    }
                    catch (System.Exception ex)
                    {
                        _logger.Error($"[AutoEmu] Failed: {ex.Message}");
                        _notificationService.ShowError($"Emulator failed for {downloadItem.GameName}: {ex.Message}");
                    }
                }

                return;
            }

            // Check if auto-install is enabled
            var settings = _settingsService.LoadSettings();
            if (settings.AutoInstallAfterDownload && !string.IsNullOrEmpty(downloadItem.DestinationPath) && File.Exists(downloadItem.DestinationPath))
            {
                // Auto-install the downloaded file
                await InstallFile(downloadItem.DestinationPath);
            }
            }
            catch (Exception ex)
            {
                _logger.Error($"[OnDownloadCompleted] Unhandled exception: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task RefreshDownloadedFiles()
        {
            var settings = _settingsService.LoadSettings();

            if (string.IsNullOrEmpty(settings.DownloadsPath) || !Directory.Exists(settings.DownloadsPath))
            {
                DownloadedFiles.Clear();
                StatusMessage = "No downloads folder configured";
                return;
            }

            try
            {
                var files = await Task.Run(() =>
                    Directory.GetFiles(settings.DownloadsPath, "*.zip")
                        .OrderByDescending(f => File.GetCreationTime(f))
                        .ToList());

                DownloadedFiles = new ObservableCollection<string>(files);
                StatusMessage = files.Count > 0 ? $"{files.Count} file(s) ready to install" : "No downloaded files";
            }
            catch (System.Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        private void RefreshHistory()
        {
            DownloadHistory = new ObservableCollection<DownloadHistoryEntry>(
                _downloadHistoryService.GetHistory());
        }

        [RelayCommand]
        private void ClearHistory()
        {
            _downloadHistoryService.ClearHistory();
            DownloadHistory.Clear();
            _notificationService.ShowSuccess("Download history cleared");
        }

        [RelayCommand]
        private async Task InstallFile(string filePath)
        {
            if (IsInstalling)
            {
                MessageBoxHelper.Show(
                    "Another installation is in progress",
                    "Please Wait",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            IsInstalling = true;
            var fileName = Path.GetFileName(filePath);
            StatusMessage = $"Installing {fileName}...";

            try
            {
                var settings = _settingsService.LoadSettings();
                var appId = Path.GetFileNameWithoutExtension(filePath);

                if (settings.Mode == ToolMode.DepotDownloader)
                {
                    // Phase 1: Prepare depot data (lua parsing, Steam metadata, DLC merge)
                    var prep = await _depotInstallOrchestrator.PrepareDepotDataAsync(
                        filePath, appId,
                        status => StatusMessage = status,
                        error =>
                        {
                            MessageBoxHelper.Show(error, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        });

                    if (prep == null)
                    {
                        StatusMessage = "Installation cancelled";
                        IsInstalling = false;
                        return;
                    }

                    // Cache lua data (DLC names, game name) for Fix Game
                    _fixGameService.CacheFromLua(prep.LuaContent, appId);

                    // Language selection dialog
                    if (prep.AvailableLanguages.Count == 0)
                    {
                        _notificationService.ShowWarning("No languages found in depot metadata. Using all depots.");
                    }

                    StatusMessage = "Waiting for language selection...";
                    var languageDialog = new LanguageSelectionDialog(prep.AvailableLanguages);
                    var languageResult = languageDialog.ShowDialog();

                    if (languageResult != true || string.IsNullOrEmpty(languageDialog.SelectedLanguage))
                    {
                        StatusMessage = "Installation cancelled";
                        IsInstalling = false;
                        return;
                    }

                    // Filter depots by language
                    var filteredDepotIds = _depotInstallOrchestrator.FilterDepotsByLanguage(prep, languageDialog.SelectedLanguage);

                    if (filteredDepotIds.Count == 0)
                    {
                        _notificationService.ShowWarning("Language filter returned no depots. Showing all available depots.");
                        filteredDepotIds = prep.ParsedDepotKeys.Keys.ToList();
                    }

                    StatusMessage = $"Found {filteredDepotIds.Count} depots. Preparing depot selection...";

                    // Depot selection dialog
                    var depotsForSelection = _depotInstallOrchestrator.BuildDepotsForSelection(prep, filteredDepotIds);

                    StatusMessage = "Waiting for depot selection...";
                    var depotDialog = new DepotSelectionDialog(depotsForSelection);
                    var depotResult = depotDialog.ShowDialog();

                    if (depotResult != true || depotDialog.SelectedDepotIds.Count == 0)
                    {
                        StatusMessage = "Installation cancelled";
                        IsInstalling = false;
                        return;
                    }

                    // Show launch config selection dialog using data fetched before disconnect
                    List<Views.Dialogs.LaunchConfigItem>? selectedLaunchConfigs = null;
                    if (prep.LaunchConfigs.Count > 0)
                    {
                        var launchItems = new List<Views.Dialogs.LaunchConfigItem>();
                        foreach (var lc in prep.LaunchConfigs)
                        {
                            // Filter: Windows only unless OS filter disabled
                            if (!settings.DisableDepotOsFilter &&
                                !string.IsNullOrEmpty(lc.OsType) && lc.OsType != "windows")
                                continue;

                            // Skip non-default beta launch configs (keep "default" and empty)
                            if (!string.IsNullOrEmpty(lc.BetaKey) && lc.BetaKey != "default")
                                continue;

                            launchItems.Add(new Views.Dialogs.LaunchConfigItem
                            {
                                Executable = lc.Executable,
                                Arguments = lc.Arguments,
                                Description = lc.Description,
                                WorkingDir = "", // Ignore PICS workingdir — always launch from game root
                                OsType = string.IsNullOrEmpty(lc.OsType) ? "windows" : lc.OsType,
                                BetaKey = lc.BetaKey
                            });
                        }

                        // Remove entries with no arguments — no bat needed, user can just run the exe
                        launchItems.RemoveAll(lc => string.IsNullOrWhiteSpace(lc.Arguments));

                        if (launchItems.Count == 1)
                        {
                            // Only one option — auto-select, skip dialog
                            launchItems[0].IsSelected = true;
                            selectedLaunchConfigs = launchItems;
                            _logger.Info($"Auto-selected single launch config: {launchItems[0].Executable}");
                        }
                        else if (launchItems.Count > 1)
                        {
                            StatusMessage = "Waiting for launch config selection...";
                            var launchDialog = new Views.Dialogs.LaunchConfigDialog(launchItems);
                            var launchResult = launchDialog.ShowDialog();
                            if (launchResult == true && !launchDialog.Skipped)
                                selectedLaunchConfigs = launchDialog.SelectedItems;
                        }
                    }

                    // Phase 2: Start the download
                    var downloadResult = await _depotInstallOrchestrator.StartDownloadAsync(
                        prep, filePath, depotDialog.SelectedDepotIds,
                        status => StatusMessage = status,
                        error =>
                        {
                            MessageBoxHelper.Show(error, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        });

                    if (downloadResult == null)
                    {
                        StatusMessage = "Installation cancelled";
                        IsInstalling = false;
                        return;
                    }

                    // Save selected launch configs for Fix Game to use after download
                    if (selectedLaunchConfigs != null && selectedLaunchConfigs.Count > 0)
                    {
                        var launchJson = Newtonsoft.Json.JsonConvert.SerializeObject(selectedLaunchConfigs.Select(lc => new
                        {
                            executable = lc.Executable,
                            arguments = lc.Arguments,
                            description = lc.Description,
                            workingdir = lc.WorkingDir,
                            ostype = lc.OsType
                        }));
                        var fixCache = new Services.FixGame.FixGameCacheService();
                        fixCache.SavePicsJson(appId + "_launch", launchJson);
                        _logger.Info($"Saved {selectedLaunchConfigs.Count} launch config(s) for Fix Game");
                    }

                    // Save cloud save dirs for Fix Game
                    if (prep.CloudSaveDirs.Count > 0)
                    {
                        var cloudJson = Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            saves = prep.CloudSaveDirs.Select(s => new { root = s.Root, path = s.Path, platforms = s.Platforms }),
                            overrides = prep.CloudSaveOverrides.Select(o => new
                            {
                                root_original = o.RootOriginal, root_new = o.RootNew,
                                path_after_root = o.PathAfterRoot, platform = o.Platform,
                                transforms = o.Transforms.Select(t => new { find = t.Find, replace = t.Replace })
                            })
                        });
                        var fixCache2 = new Services.FixGame.FixGameCacheService();
                        fixCache2.SavePicsJson(appId + "_cloudsave", cloudJson);
                        _logger.Info($"Saved {prep.CloudSaveDirs.Count} cloud save dir(s) for Fix Game");
                    }

                    _notificationService.ShowSuccess($"Download started for {downloadResult.GameName}!\n\nCheck the Downloads tab to monitor progress.", "Download Started");
                    StatusMessage = "Download started - check progress below";

                    if (settings.DeleteZipAfterInstall)
                    {
                        File.Delete(filePath);
                        await RefreshDownloadedFiles();
                    }

                    IsInstalling = false;
                    return;
                }

                StatusMessage = $"Installing files...";
                await _fileInstallService.InstallFromZipAsync(filePath, message => StatusMessage = message);

                // Intentionally NOT calling ManifestStorageService.StoreManifest here.
                // That writes a {installPath}/.hubcapmanifestapp/install.json marker, which
                // is a DepotDownloader concept (tags a real install directory). In
                // SteamTools mode the lua IS the install and installPath would be the
                // Steam stplug-in folder, so the marker would clutter stplug-in. We
                // rely purely on scanning stplug-in for lua files instead.

                _notificationService.ShowSuccess($"{fileName} has been installed successfully! Restart Steam for changes to take effect.", "Installation Complete");
                StatusMessage = $"{fileName} installed successfully";

                _libraryRefreshService.NotifyGameInstalled(appId);

                if (settings.DeleteZipAfterInstall)
                {
                    File.Delete(filePath);
                    await RefreshDownloadedFiles();
                }
            }
            catch (System.Exception ex)
            {
                StatusMessage = $"Installation failed: {ex.Message}";
                MessageBoxHelper.Show(
                    $"Failed to install {fileName}: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                IsInstalling = false;
            }
        }

        [RelayCommand]
        private void CancelDownload(DownloadItem item)
        {
            _downloadService.CancelDownload(item.Id);
            StatusMessage = $"Cancelled: {item.GameName}";
        }

        [RelayCommand]
        private void RemoveDownload(DownloadItem item)
        {
            _downloadService.RemoveDownload(item);
        }

        [RelayCommand]
        private void ClearCompleted()
        {
            _downloadService.ClearCompletedDownloads();
        }

        [RelayCommand]
        private void DeleteFile(string filePath)
        {
            var result = MessageBoxHelper.Show(
                $"Are you sure you want to delete {Path.GetFileName(filePath)}?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    File.Delete(filePath);
                    _ = RefreshDownloadedFiles();
                    StatusMessage = "File deleted";
                }
                catch (System.Exception ex)
                {
                    MessageBoxHelper.Show(
                        $"Failed to delete file: {ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        [RelayCommand]
        private void OpenDownloadsFolder()
        {
            var settings = _settingsService.LoadSettings();

            if (!string.IsNullOrEmpty(settings.DownloadsPath) && Directory.Exists(settings.DownloadsPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = settings.DownloadsPath,
                    UseShellExecute = true
                });
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                _downloadService.DownloadCompleted -= OnDownloadCompleted;
                _downloadService.DownloadFailed -= OnDownloadFailed;
            }

            _disposed = true;
        }
    }
}
