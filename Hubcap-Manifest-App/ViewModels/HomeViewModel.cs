using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HubcapManifestApp.Helpers;
using HubcapManifestApp.Models;
using HubcapManifestApp.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace HubcapManifestApp.ViewModels
{
    public partial class HomeViewModel : ObservableObject
    {
        private readonly SettingsService _settingsService;
        private readonly SteamService _steamService;
        private readonly NotificationService _notificationService;
        private readonly ManifestApiService _manifestApiService;
        private readonly DownloadHistoryService _downloadHistoryService;
        private readonly LibraryDatabaseService _dbService;
        private readonly CacheService _cacheService;
        private readonly DownloadService _downloadService;

        [ObservableProperty] private string _currentModeText = string.Empty;
        [ObservableProperty] private string _currentModeDescription = string.Empty;

        // Dashboard stats
        [ObservableProperty] private int _libraryCount;
        [ObservableProperty] private int _luaCount;
        [ObservableProperty] private int _steamGameCount;
        [ObservableProperty] private string _totalSize = "0 B";
        [ObservableProperty] private int _dailyUsage;
        [ObservableProperty] private int _dailyLimit;
        [ObservableProperty] private int _dailyRemaining;
        [ObservableProperty] private bool _hasApiStats;
        [ObservableProperty] private string _steamPath = "";
        [ObservableProperty] private ObservableCollection<DownloadHistoryEntry> _recentDownloads = new();
        [ObservableProperty] private ObservableCollection<LibraryGame> _recentlyAdded = new();

        public Action? OnModeToggled { get; set; }

        public HomeViewModel(
            SettingsService settingsService,
            SteamService steamService,
            NotificationService notificationService,
            ManifestApiService manifestApiService,
            DownloadHistoryService downloadHistoryService,
            LibraryDatabaseService dbService,
            CacheService cacheService,
            DownloadService downloadService)
        {
            _settingsService = settingsService;
            _steamService = steamService;
            _notificationService = notificationService;
            _manifestApiService = manifestApiService;
            _downloadHistoryService = downloadHistoryService;
            _dbService = dbService;
            _cacheService = cacheService;
            _downloadService = downloadService;

            RefreshMode();
        }

        public void RefreshMode()
        {
            var settings = _settingsService.LoadSettings();

            if (settings.Mode == ToolMode.SteamTools)
            {
                CurrentModeText = "Current Mode: SteamTools";
            }
            else if (settings.Mode == ToolMode.DepotDownloader)
            {
                CurrentModeText = "Current Mode: DepotDownloader";
            }
            else
            {
                CurrentModeText = "Current Mode: Unknown";
            }
        }

        [RelayCommand]
        private async Task LoadDashboard()
        {
            var settings = _settingsService.LoadSettings();

            // Library stats — sync DB + LINQ on background thread
            try
            {
                var (count, luaCount, steamCount, totalBytes) = await Task.Run(() =>
                {
                    var items = _dbService.GetAllLibraryItems();
                    return (
                        items.Count,
                        items.Count(i => i.ItemType == LibraryItemType.Lua),
                        items.Count(i => i.ItemType == LibraryItemType.SteamGame),
                        items.Sum(i => i.SizeBytes)
                    );
                });
                LibraryCount = count;
                LuaCount = luaCount;
                SteamGameCount = steamCount;
                TotalSize = FormatHelper.FormatBytes(totalBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HomeViewModel] Failed to load library stats: {ex.Message}");
            }

            // Steam path
            SteamPath = _steamService.GetSteamPath() ?? "Not configured";

            // Recent downloads — sync file I/O on background thread
            var history = await Task.Run(() => _downloadHistoryService.GetHistory());
            RecentDownloads = new ObservableCollection<DownloadHistoryEntry>(history.Take(5));

            // Recently added to store
            try
            {
                if (!string.IsNullOrEmpty(settings.ApiKey))
                {
                    var result = await _manifestApiService.GetLibraryAsync(settings.ApiKey, limit: 6, offset: 0, sortBy: "updated");
                    if (result?.Games != null)
                    {
                        RecentlyAdded = new ObservableCollection<LibraryGame>(result.Games);
                        // Load icons and batch-update on UI thread
                        var iconUpdates = new List<(LibraryGame game, string path)>();
                        foreach (var game in RecentlyAdded.Where(g => !string.IsNullOrEmpty(g.HeaderImage)))
                        {
                            var iconPath = await _cacheService.GetIconAsync(game.GameId, game.HeaderImage);
                            if (!string.IsNullOrEmpty(iconPath))
                                iconUpdates.Add((game, iconPath));
                        }
                        if (iconUpdates.Count > 0)
                        {
                            Application.Current?.Dispatcher.Invoke(() =>
                            {
                                foreach (var (game, path) in iconUpdates)
                                    game.CachedIconPath = path;
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HomeViewModel] Failed to load recently added: {ex.Message}");
            }

            // API stats
            try
            {
                if (!string.IsNullOrEmpty(settings.ApiKey))
                {
                    var stats = await _manifestApiService.GetUserStatsAsync(settings.ApiKey);
                    if (stats != null)
                    {
                        DailyUsage = stats.DailyUsage;
                        DailyLimit = stats.DailyLimit;
                        DailyRemaining = stats.Remaining;
                        HasApiStats = true;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HomeViewModel] Failed to load API stats: {ex.Message}");
            }
        }

        [RelayCommand]
        private void ToggleMode()
        {
            var settings = _settingsService.LoadSettings();
            settings.Mode = settings.Mode == ToolMode.SteamTools ? ToolMode.DepotDownloader : ToolMode.SteamTools;
            _settingsService.SaveSettings(settings);
            RefreshMode();
            OnModeToggled?.Invoke();
        }

        [RelayCommand]
        private void DownloadRecentGame(LibraryGame game)
        {
            if (game == null || !game.ManifestAvailable) return;
            var settings = _settingsService.LoadSettings();
            if (string.IsNullOrEmpty(settings.ApiKey))
            {
                _notificationService.ShowWarning("Please enter API key in settings");
                return;
            }

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
            _notificationService.ShowSuccess($"Queued {game.GameName} for download");
        }

        [RelayCommand]
        private void LaunchSteam()
        {
            try
            {
                var steamPath = _steamService.GetSteamPath();
                if (string.IsNullOrEmpty(steamPath))
                {
                    _notificationService.ShowError("Steam path is not configured. Please set it in Settings.");
                    return;
                }

                var steamExePath = Path.Combine(steamPath, SteamPaths.SteamExe);
                if (!File.Exists(steamExePath))
                {
                    _notificationService.ShowError($"Steam.exe not found at: {steamExePath}");
                    return;
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = steamExePath,
                    UseShellExecute = true,
                    WorkingDirectory = steamPath
                });
                _notificationService.ShowSuccess("Steam launched!");
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to launch Steam: {ex.Message}");
            }
        }

    }
}
