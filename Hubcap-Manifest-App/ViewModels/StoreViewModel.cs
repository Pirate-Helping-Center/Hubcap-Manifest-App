using HubcapManifestApp.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HubcapManifestApp.Models;
using HubcapManifestApp.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace HubcapManifestApp.ViewModels
{
    public partial class StoreViewModel : ObservableObject, IDisposable
    {
        private bool _disposed;
        private readonly ManifestApiService _manifestApiService;
        private readonly DownloadService _downloadService;
        private readonly SettingsService _settingsService;
        private readonly CacheService _cacheService;
        private readonly NotificationService _notificationService;
        private readonly ManifestStorageService _manifestStorageService;
        private readonly AppListCacheService _appListCacheService;
        private readonly LibraryDatabaseService _libraryDatabaseService;
        private readonly SteamService _steamService;
        private readonly LibraryRefreshService _libraryRefreshService;
        private readonly DepotFilterService _depotFilterService;
        private readonly LuaParser _luaParser;
        private readonly SemaphoreSlim _iconLoadSemaphore = new SemaphoreSlim(10, 10);
        private CancellationTokenSource? _debounceTokenSource;

        [ObservableProperty]
        private ObservableCollection<LibraryGame> _games = new();

        [ObservableProperty]
        private string _searchQuery = string.Empty;

        [ObservableProperty]
        private bool _searchByAppId;

        [ObservableProperty]
        private ObservableCollection<AppListEntry> _suggestions = new();

        [ObservableProperty]
        private bool _showSuggestions;

        [ObservableProperty]
        private AppListEntry? _selectedSuggestion;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _statusMessage = "Browse available games from the library";

        // True when the app is in SteamTools mode. Used to hide the Update button for
        // lua-installed games (there's no "update" concept — the lua itself is the install).
        [ObservableProperty]
        private bool _isSteamToolsMode;

        [ObservableProperty]
        private bool _isSelectMode;

        [ObservableProperty]
        private int _selectedCount;

        [ObservableProperty]
        private string _sortBy = "updated"; // "updated" or "name"

        [ObservableProperty]
        private int _totalCount;

        [ObservableProperty]
        private int _currentOffset;

        [ObservableProperty]
        private bool _hasMore;

        [ObservableProperty]
        private int _currentPage = 1;

        [ObservableProperty]
        private int _totalPages;

        [ObservableProperty]
        private bool _canGoNext;

        [ObservableProperty]
        private bool _canGoPrevious;

        [ObservableProperty]
        private bool _isListView;

        [ObservableProperty]
        private bool _hideHeaderImages;

        [ObservableProperty]
        private string _goToPageText = string.Empty;

        [ObservableProperty]
        private ObservableCollection<int> _pageNumbers = new();

        private int PageSize => _settingsService.LoadSettings().StorePageSize;

        public Action? ScrollToTopAction { get; set; }

        public StoreViewModel(
            ManifestApiService manifestApiService,
            DownloadService downloadService,
            SettingsService settingsService,
            CacheService cacheService,
            NotificationService notificationService,
            ManifestStorageService manifestStorageService,
            AppListCacheService appListCacheService,
            LibraryDatabaseService libraryDatabaseService,
            SteamService steamService,
            LibraryRefreshService libraryRefreshService,
            DepotFilterService depotFilterService,
            LuaParser luaParser)
        {
            _manifestApiService = manifestApiService;
            _downloadService = downloadService;
            _settingsService = settingsService;
            _cacheService = cacheService;
            _notificationService = notificationService;
            _manifestStorageService = manifestStorageService;
            _appListCacheService = appListCacheService;
            _libraryDatabaseService = libraryDatabaseService;
            _steamService = steamService;
            _libraryRefreshService = libraryRefreshService;
            _depotFilterService = depotFilterService;
            _luaParser = luaParser;

            _settingsService.SettingsChanged += OnSettingsChanged;
            _libraryRefreshService.GameInstalled += OnLibraryGameInstalled;
            _libraryRefreshService.GameUninstalled += OnLibraryGameUninstalled;

            // Auto-load games on startup
            _ = InitializeAsync();
        }

        private void OnLibraryGameInstalled(object? sender, GameInstalledEventArgs e)
        {
            // A game just got installed via this app — re-check install status on the
            // current in-memory game list so the Store card flips immediately.
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (Games.Count > 0) UpdateInstallationStatus(Games.ToList());
            });
        }

        private void OnLibraryGameUninstalled(object? sender, string appId)
        {
            // Mirror for uninstalls — the Library fires this after a successful uninstall
            // so the Store card drops its "Installed" state without a manual reload.
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (Games.Count > 0) UpdateInstallationStatus(Games.ToList());
            });
        }

        private void OnSettingsChanged(object? sender, Models.AppSettings settings)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsSteamToolsMode = settings.Mode == Models.ToolMode.SteamTools;
            });
        }

        private async Task InitializeAsync()
        {
            var settings = _settingsService.LoadSettings();
            IsListView = settings.StoreListView;
            HideHeaderImages = settings.HideHeaderImages;
            IsSteamToolsMode = settings.Mode == Models.ToolMode.SteamTools;
            if (!string.IsNullOrEmpty(settings.ApiKey))
            {
                await LoadGamesAsync();
            }
            else
            {
                StatusMessage = "API key required - Please configure in Settings";
            }
        }

        public void OnNavigatedTo()
        {
            var settings = _settingsService.LoadSettings();

            // Re-evaluate IsInstalled on every card we already have in memory so
            // anything uninstalled since last load drops the stale flag without
            // requiring a reload. Near-instant (just a stplug-in directory scan).
            if (Games.Count > 0)
            {
                UpdateInstallationStatus(Games.ToList());
            }

            if (string.IsNullOrEmpty(settings.ApiKey))
            {
                // Show warning popup when user navigates to Store without API key
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBoxHelper.Show(
                        "An API key is required to use the Store.\n\nPlease go to Settings and enter your API key to browse and download games from the library.",
                        "API Key Required",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                });
            }
        }

        [RelayCommand]
        private void ToggleView()
        {
            IsListView = !IsListView;
            var settings = _settingsService.LoadSettings();
            settings.StoreListView = IsListView;
            _settingsService.SaveSettings(settings);
        }

        partial void OnSearchQueryChanged(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                ShowSuggestions = false;
                Suggestions.Clear();
                if (Games.Count > 0)
                {
                    _ = LoadGamesAsync();
                }
                return;
            }

            _debounceTokenSource?.Cancel();
            _debounceTokenSource = new CancellationTokenSource();
            var token = _debounceTokenSource.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(150, token);
                    if (token.IsCancellationRequested) return;

                    var results = _appListCacheService.Search(value, 8);

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        Suggestions.Clear();
                        foreach (var result in results)
                        {
                            Suggestions.Add(result);
                        }
                        ShowSuggestions = Suggestions.Count > 0;
                    });
                }
                catch (TaskCanceledException)
                {
                }
            }, token);
        }

        [RelayCommand]
        private void SelectSuggestion(AppListEntry? suggestion)
        {
            if (suggestion == null) return;

            SearchQuery = suggestion.AppId.ToString();
            SearchByAppId = true;
            ShowSuggestions = false;
            _ = SearchGames();
        }

        [RelayCommand]
        private void HideSuggestions()
        {
            ShowSuggestions = false;
        }

        [RelayCommand]
        private async Task LoadGames()
        {
            var settings = _settingsService.LoadSettings();

            if (string.IsNullOrEmpty(settings.ApiKey))
            {
                StatusMessage = "Please enter API key in settings";
                MessageBoxHelper.Show(
                    "An API key is required to use the Store.\n\nPlease go to Settings and enter your API key to browse and download games from the library.",
                    "API Key Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Reset to first page
            CurrentPage = 1;
            CurrentOffset = 0;
            Games.Clear();

            await LoadGamesAsync();
        }

        [RelayCommand]
        private async Task NextPage()
        {
            if (!CanGoNext || IsLoading) return;

            CurrentPage++;
            CurrentOffset = (CurrentPage - 1) * PageSize;
            Games.Clear();
            await LoadGamesAsync();
            ScrollToTopAction?.Invoke();
        }

        [RelayCommand]
        private async Task PreviousPage()
        {
            if (!CanGoPrevious || IsLoading) return;

            CurrentPage--;
            CurrentOffset = (CurrentPage - 1) * PageSize;
            Games.Clear();
            await LoadGamesAsync();
            ScrollToTopAction?.Invoke();
        }

        [RelayCommand]
        private async Task GoToPage(int pageNumber)
        {
            if (pageNumber < 1 || pageNumber > TotalPages || pageNumber == CurrentPage || IsLoading) return;

            CurrentPage = pageNumber;
            CurrentOffset = (CurrentPage - 1) * PageSize;
            Games.Clear();
            await LoadGamesAsync();
            ScrollToTopAction?.Invoke();
        }

        [RelayCommand]
        private async Task GoToPageFromText()
        {
            if (string.IsNullOrWhiteSpace(GoToPageText) || IsLoading) return;

            if (int.TryParse(GoToPageText, out int pageNumber))
            {
                if (pageNumber >= 1 && pageNumber <= TotalPages && pageNumber != CurrentPage)
                {
                    CurrentPage = pageNumber;
                    CurrentOffset = (CurrentPage - 1) * PageSize;
                    Games.Clear();
                    await LoadGamesAsync();
                    ScrollToTopAction?.Invoke();
                }
            }
            GoToPageText = string.Empty;
        }

        private void UpdatePageNumbers()
        {
            PageNumbers.Clear();
            if (TotalPages <= 0) return;

            int maxVisiblePages = 7;
            int startPage = 1;
            int endPage = TotalPages;

            if (TotalPages > maxVisiblePages)
            {
                int halfVisible = maxVisiblePages / 2;
                startPage = System.Math.Max(1, CurrentPage - halfVisible);
                endPage = System.Math.Min(TotalPages, startPage + maxVisiblePages - 1);

                if (endPage - startPage < maxVisiblePages - 1)
                {
                    startPage = System.Math.Max(1, endPage - maxVisiblePages + 1);
                }
            }

            for (int i = startPage; i <= endPage; i++)
            {
                PageNumbers.Add(i);
            }
        }

        [RelayCommand]
        private async Task SearchGames()
        {
            var settings = _settingsService.LoadSettings();

            if (string.IsNullOrEmpty(settings.ApiKey))
            {
                StatusMessage = "Please enter API key in settings";
                return;
            }

            if (string.IsNullOrWhiteSpace(SearchQuery))
            {
                // If search is empty, load library normally
                await LoadGames();
                return;
            }

            if (SearchQuery.Length < 2)
            {
                StatusMessage = "Enter at least 2 characters to search";
                return;
            }

            IsLoading = true;
            StatusMessage = "Searching...";
            Games.Clear();

            try
            {
                var result = await _manifestApiService.SearchLibraryAsync(SearchQuery, settings.ApiKey, 100, SearchByAppId);

                if (result != null && result.Results.Count > 0)
                {
                    foreach (var game in result.Results)
                    {
                        Games.Add(game);
                    }

                    TotalCount = result.TotalMatches;
                    CurrentPage = 1;
                    TotalPages = 1;
                    CanGoPrevious = false;
                    CanGoNext = false;
                    StatusMessage = $"Found {result.ReturnedCount} of {result.TotalMatches} matching games";

                    UpdateInstallationStatus(result.Results);

                    _ = LoadAllGameIconsAsync(result.Results);
                }
                else
                {
                    StatusMessage = "No games found";
                    TotalCount = 0;
                    CurrentPage = 1;
                    TotalPages = 0;
                    CanGoPrevious = false;
                    CanGoNext = false;
                }
            }
            catch (System.Exception ex)
            {
                StatusMessage = $"Search failed: {ex.Message}";
                MessageBoxHelper.Show(
                    $"Failed to search: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        partial void OnSortByChanged(string value)
        {
            if (Games.Count == 0) return;
            CurrentOffset = 0;
            Games.Clear();
            _ = LoadGamesAsync();
        }

        [RelayCommand]
        private void ToggleSelectMode()
        {
            IsSelectMode = !IsSelectMode;
            if (!IsSelectMode)
            {
                foreach (var game in Games) game.IsSelected = false;
                SelectedCount = 0;
            }
        }

        [RelayCommand]
        private void SelectAll()
        {
            foreach (var game in Games.Where(g => g.ManifestAvailable && !g.IsInstalled))
                game.IsSelected = true;
            SelectedCount = Games.Count(g => g.IsSelected);
        }

        [RelayCommand]
        private void DeselectAll()
        {
            foreach (var game in Games) game.IsSelected = false;
            SelectedCount = 0;
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
                _settingsService.SettingsChanged -= OnSettingsChanged;
                _libraryRefreshService.GameInstalled -= OnLibraryGameInstalled;
                _libraryRefreshService.GameUninstalled -= OnLibraryGameUninstalled;
                _debounceTokenSource?.Dispose();
                _iconLoadSemaphore.Dispose();
            }

            _disposed = true;
        }
    }
}
