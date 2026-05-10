using HubcapManifestApp.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HubcapManifestApp.Models;
using HubcapManifestApp.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace HubcapManifestApp.ViewModels
{
    public partial class LibraryViewModel : ObservableObject, IDisposable
    {
        private bool _disposed;
        private readonly FileInstallService _fileInstallService;
        private readonly SteamService _steamService;
        private readonly SteamGamesService _steamGamesService;
        private readonly ManifestApiService _manifestApiService;
        private readonly SettingsService _settingsService;
        private readonly CacheService _cacheService;
        private readonly NotificationService _notificationService;
        private readonly LuaFileManager _luaFileManager;
        private readonly ArchiveExtractionService _archiveExtractor;
        private readonly SteamApiService _steamApiService;
        private readonly LoggerService _logger;
        private readonly LibraryDatabaseService _dbService;
        private readonly LibraryRefreshService _refreshService;
        private readonly RecentGamesService _recentGamesService;
        private readonly ImageCacheService _imageCacheService;
        private readonly DownloadService _downloadService;
        private readonly HubcapManifestApp.Services.FixGame.FixGameService _fixGameService;

        private List<LibraryItem> _allItems = new();
        private readonly object _allItemsLock = new();

        [ObservableProperty]
        private ObservableCollection<LibraryItem> _displayedItems = new();

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _statusMessage = "No items";

        [ObservableProperty]
        private string _searchQuery = string.Empty;

        [ObservableProperty]
        private string _selectedFilter = "All";

        [ObservableProperty]
        private string _selectedSort = "Name";

        [ObservableProperty]
        private int _totalLua;

        [ObservableProperty]
        private int _totalSteamGames;

        [ObservableProperty]
        private long _totalSize;

        [ObservableProperty]
        private bool _showLua = true;

        [ObservableProperty]
        private bool _showSteamGames = true;

        [ObservableProperty]
        private bool _isSelectMode;

        [ObservableProperty]
        private ObservableCollection<string> _filterOptions = new();

        [ObservableProperty]
        private bool _isListView;

        [ObservableProperty]
        private bool _hideHeaderImages;

        // Mirrors AppSettings.Mode == SteamTools. Used to show the Enable/Disable Updates
        // buttons that only make sense in SteamTools mode. Refreshed via SettingsChanged.
        [ObservableProperty]
        private bool _isSteamToolsMode;

        [ObservableProperty]
        private int _currentPage = 1;

        [ObservableProperty]
        private int _totalPages = 1;

        [ObservableProperty]
        private int _itemsPerPage = 20;

        [ObservableProperty]
        private bool _canGoPrevious;

        [ObservableProperty]
        private bool _canGoNext;

        public List<string> SortOptions { get; } = new() { "Name", "Size", "Install Date", "Last Updated" };

        public LibraryViewModel(
            FileInstallService fileInstallService,
            SteamService steamService,
            SteamGamesService steamGamesService,
            ManifestApiService manifestApiService,
            SettingsService settingsService,
            CacheService cacheService,
            NotificationService notificationService,
            LoggerService logger,
            LibraryDatabaseService dbService,
            LibraryRefreshService refreshService,
            RecentGamesService recentGamesService,
            DownloadService downloadService,
            SteamApiService steamApiService,
            ArchiveExtractionService archiveExtractor,
            ImageCacheService imageCacheService,
            HubcapManifestApp.Services.FixGame.FixGameService fixGameService)
        {
            _fixGameService = fixGameService;
            _fileInstallService = fileInstallService;
            _steamService = steamService;
            _logger = logger;
            _steamGamesService = steamGamesService;
            _manifestApiService = manifestApiService;
            _settingsService = settingsService;
            _cacheService = cacheService;
            _notificationService = notificationService;
            _dbService = dbService;
            _refreshService = refreshService;
            _recentGamesService = recentGamesService;
            _downloadService = downloadService;
            _archiveExtractor = archiveExtractor;
            _imageCacheService = imageCacheService;

            var stpluginPath = _steamService.GetStPluginPath() ?? "";
            _luaFileManager = new LuaFileManager(stpluginPath, logger);

            var settings = _settingsService.LoadSettings();
            _steamApiService = steamApiService;
            IsListView = settings.LibraryListView;
            HideHeaderImages = settings.HideHeaderImages;
            ItemsPerPage = settings.LibraryPageSize;
            IsSteamToolsMode = settings.Mode == ToolMode.SteamTools;

            _refreshService.GameInstalled += OnGameInstalled;
            _settingsService.SettingsChanged += OnSettingsChanged;
        }

        private void OnSettingsChanged(object? sender, AppSettings settings)
        {
            // Marshal to UI thread; SaveSettings can be called from worker threads.
            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsSteamToolsMode = settings.Mode == ToolMode.SteamTools;
                ItemsPerPage = settings.LibraryPageSize;
                ApplyFilters();
            });
        }

        [RelayCommand]
        private void ToggleView()
        {
            IsListView = !IsListView;
            var settings = _settingsService.LoadSettings();
            settings.LibraryListView = IsListView;
            _settingsService.SaveSettings(settings);
        }

        private async void OnGameInstalled(object? sender, GameInstalledEventArgs e)
        {
            try
            {
                await AddGameToLibraryAsync(e.AppId);
            }
            catch (Exception ex)
            {
                _logger.Error($"[OnGameInstalled] Failed for AppId {e.AppId}: {ex.Message}");
            }
        }

        partial void OnSearchQueryChanged(string value)
        {
            ApplyFilters();
        }

        partial void OnSelectedFilterChanged(string value)
        {
            UpdateVisibilityFilters();
            ApplyFilters();
        }

        partial void OnSelectedSortChanged(string value)
        {
            ApplyFilters();
        }

        private void UpdateVisibilityFilters()
        {
            ShowLua = SelectedFilter is "All" or "Lua Only";
            ShowSteamGames = SelectedFilter is "All" or "Steam Games Only";
        }

        private void ApplyFilters()
        {
            // Do filtering/sorting on background thread
            Task.Run(() =>
            {
                List<LibraryItem> snapshot;
                lock (_allItemsLock)
                {
                    snapshot = _allItems.ToList();
                }
                var filtered = snapshot.AsEnumerable();

                // Filter by type
                if (!ShowLua)
                    filtered = filtered.Where(i => i.ItemType != LibraryItemType.Lua);
                if (!ShowSteamGames)
                    filtered = filtered.Where(i => i.ItemType != LibraryItemType.SteamGame);

                // Search filter
                if (!string.IsNullOrWhiteSpace(SearchQuery))
                {
                    var query = SearchQuery.Trim();
                    if (uint.TryParse(query, out _))
                    {
                        filtered = filtered.Where(i => i.AppId == query);
                    }
                    else
                    {
                        var lowerQuery = query.ToLower();
                        filtered = filtered.Where(i =>
                            i.Name.ToLower().Contains(lowerQuery) ||
                            i.AppId.ToLower().Contains(lowerQuery) ||
                            i.Description.ToLower().Contains(lowerQuery));
                    }
                }

                // Sort
                filtered = SelectedSort switch
                {
                    "Size" => filtered.OrderByDescending(i => i.SizeBytes),
                    "Install Date" => filtered.OrderByDescending(i => i.InstallDate),
                    "Last Updated" => filtered.OrderByDescending(i => i.LastUpdated),
                    _ => filtered.OrderBy(i => i.Name)
                };

                var filteredList = filtered.ToList();

                // Update UI on UI thread
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    // Handle pagination
                    List<LibraryItem> pagedItems;

                    if (ItemsPerPage <= 0)
                    {
                        // Show all items (no pagination)
                        pagedItems = filteredList;
                        TotalPages = 1;
                        CurrentPage = 1;
                    }
                    else
                    {
                        // Calculate pagination
                        TotalPages = (int)Math.Ceiling((double)filteredList.Count / ItemsPerPage);
                        if (TotalPages == 0) TotalPages = 1;

                        // Ensure current page is within bounds
                        if (CurrentPage > TotalPages)
                            CurrentPage = TotalPages;
                        if (CurrentPage < 1)
                            CurrentPage = 1;

                        // Get items for current page
                        pagedItems = filteredList
                            .Skip((CurrentPage - 1) * ItemsPerPage)
                            .Take(ItemsPerPage)
                            .ToList();
                    }

                    // Swap the entire collection in one shot so WPF only
                    // re-measures / re-arranges once instead of N+1 times
                    // (1 Clear + N Adds = N+1 CollectionChanged events).
                    DisplayedItems = new ObservableCollection<LibraryItem>(pagedItems);

                    // Update pagination state
                    CanGoPrevious = CurrentPage > 1;
                    CanGoNext = CurrentPage < TotalPages;

                    // Update status message
                    if (ItemsPerPage <= 0)
                    {
                        StatusMessage = $"{filteredList.Count} of {_allItems.Count} item(s)";
                    }
                    else
                    {
                        StatusMessage = $"Page {CurrentPage} of {TotalPages}: Showing {DisplayedItems.Count} of {filteredList.Count} filtered item(s) ({_allItems.Count} total)";
                    }
                });
            });
        }

        [RelayCommand]
        private void NextPage()
        {
            if (CanGoNext)
            {
                CurrentPage++;
                ApplyFilters();
            }
        }

        [RelayCommand]
        private void PreviousPage()
        {
            if (CanGoPrevious)
            {
                CurrentPage--;
                ApplyFilters();
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
                _refreshService.GameInstalled -= OnGameInstalled;
                _settingsService.SettingsChanged -= OnSettingsChanged;
                _imageCacheService?.ClearCache();
            }

            _disposed = true;
        }
    }
}
