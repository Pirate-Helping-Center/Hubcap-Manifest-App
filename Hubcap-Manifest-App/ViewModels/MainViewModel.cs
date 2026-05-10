using HubcapManifestApp.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HubcapManifestApp.Services;
using HubcapManifestApp.Views;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace HubcapManifestApp.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly SteamService _steamService;
        private readonly SettingsService _settingsService;
        private readonly UpdateService _updateService;
        private readonly NotificationService _notificationService;
        private readonly Dictionary<string, UserControl> _cachedViews = new Dictionary<string, UserControl>();

        [ObservableProperty]
        private object? _currentPage;

        [ObservableProperty]
        private string _currentPageName = "Home";

        [ObservableProperty]
        private bool _showWorkshop = true;

        [ObservableProperty]
        private bool _showCloudSaves = true;

        [ObservableProperty]
        private bool _showTools = true;


        public HomeViewModel HomeViewModel { get; }
        public LuaInstallerViewModel LuaInstallerViewModel { get; }
        public LibraryViewModel LibraryViewModel { get; }
        public StoreViewModel StoreViewModel { get; }
        public DownloadsViewModel DownloadsViewModel { get; }
        public ToolsViewModel ToolsViewModel { get; }
        public WorkshopViewModel WorkshopViewModel { get; }
        public CloudDashboardViewModel CloudDashboardViewModel { get; }
        public CloudSetupViewModel CloudSetupViewModel { get; }
        public CloudProviderViewModel CloudProviderViewModel { get; }
        public CloudAppsViewModel CloudAppsViewModel { get; }
        public CloudCleanupViewModel CloudCleanupViewModel { get; }
        public CloudModeViewModel CloudModeViewModel { get; }
        public CloudPinningViewModel CloudPinningViewModel { get; }
        public CloudExperimentalViewModel CloudExperimentalViewModel { get; }
        public SettingsViewModel SettingsViewModel { get; }
        public SupportViewModel SupportViewModel { get; }

        public MainViewModel(
            SteamService steamService,
            SettingsService settingsService,
            UpdateService updateService,
            NotificationService notificationService,
            HomeViewModel homeViewModel,
            LuaInstallerViewModel luaInstallerViewModel,
            LibraryViewModel libraryViewModel,
            StoreViewModel storeViewModel,
            DownloadsViewModel downloadsViewModel,
            ToolsViewModel toolsViewModel,
            WorkshopViewModel workshopViewModel,
            CloudDashboardViewModel cloudDashboardViewModel,
            CloudSetupViewModel cloudSetupViewModel,
            CloudProviderViewModel cloudProviderViewModel,
            CloudAppsViewModel cloudAppsViewModel,
            CloudCleanupViewModel cloudCleanupViewModel,
            CloudModeViewModel cloudModeViewModel,
            CloudPinningViewModel cloudPinningViewModel,
            CloudExperimentalViewModel cloudExperimentalViewModel,
            SettingsViewModel settingsViewModel,
            SupportViewModel supportViewModel)
        {
            _steamService = steamService;
            _settingsService = settingsService;
            _updateService = updateService;
            _notificationService = notificationService;

            HomeViewModel = homeViewModel;
            LuaInstallerViewModel = luaInstallerViewModel;
            LibraryViewModel = libraryViewModel;
            StoreViewModel = storeViewModel;
            DownloadsViewModel = downloadsViewModel;
            ToolsViewModel = toolsViewModel;
            WorkshopViewModel = workshopViewModel;
            CloudDashboardViewModel = cloudDashboardViewModel;
            CloudSetupViewModel = cloudSetupViewModel;
            CloudProviderViewModel = cloudProviderViewModel;
            CloudAppsViewModel = cloudAppsViewModel;
            CloudCleanupViewModel = cloudCleanupViewModel;
            CloudModeViewModel = cloudModeViewModel;
            CloudPinningViewModel = cloudPinningViewModel;
            CloudExperimentalViewModel = cloudExperimentalViewModel;
            SettingsViewModel = settingsViewModel;
            SupportViewModel = supportViewModel;

            // Load sidebar visibility from settings
            LoadSidebarVisibility();

            // Start at Home page
            CurrentPage = GetOrCreateView("Home", () => new HomePage { DataContext = HomeViewModel });
            CurrentPageName = "Home";
            HomeViewModel.RefreshMode();

            // When mode is toggled from Home page, refresh Settings and Installer VMs
            HomeViewModel.OnModeToggled = () =>
            {
                SettingsViewModel.LoadSettings();
                LuaInstallerViewModel.RefreshMode();
            };

            // Rebuild Settings page after save to pick up layout changes
            SettingsViewModel.OnSettingsSaved = () =>
            {
                _cachedViews.Remove("Settings");
                CurrentPage = GetOrCreateView("Settings", () => new SettingsPage { DataContext = SettingsViewModel });
                LoadSidebarVisibility();
            };
        }

        private UserControl GetOrCreateView(string key, Func<UserControl> createView)
        {
            if (!_cachedViews.ContainsKey(key))
            {
                _cachedViews[key] = createView();
            }
            return _cachedViews[key];
        }

        private bool CanNavigateAway()
        {
            // Check if we're currently on settings page and have unsaved changes
            if (CurrentPageName == "Settings" && SettingsViewModel.HasUnsavedChanges)
            {
                var result = MessageBoxHelper.Show(
                    "You have unsaved changes. Do you want to leave without saving?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                return result == MessageBoxResult.Yes;
            }
            return true;
        }

        // Public method for navigation from external services (like TrayIcon)
        public void NavigateTo(string pageName)
        {
            switch (pageName.ToLower())
            {
                case "home":
                    NavigateToHome();
                    break;
                case "installer":
                    NavigateToInstaller();
                    break;
                case "library":
                    NavigateToLibrary();
                    break;
                case "store":
                    NavigateToStore();
                    break;
                case "downloads":
                    NavigateToDownloads();
                    break;
                case "workshop":
                    NavigateToWorkshop();
                    break;
                case "tools":
                    NavigateToTools();
                    break;
                case "settings":
                    NavigateToSettings();
                    break;
                case "support":
                    NavigateToSupport();
                    break;
            }
        }

        [RelayCommand]
        private void NavigateToHome()
        {
            if (!CanNavigateAway()) return;

            CurrentPage = GetOrCreateView("Home", () => new HomePage { DataContext = HomeViewModel });
            CurrentPageName = "Home";
            HomeViewModel.RefreshMode();
        }

        [RelayCommand]
        private void NavigateToInstaller()
        {
            if (!CanNavigateAway()) return;

            CurrentPage = GetOrCreateView("Installer", () => new LuaInstallerPage { DataContext = LuaInstallerViewModel });
            CurrentPageName = "Installer";
            LuaInstallerViewModel.RefreshMode();
        }

        [RelayCommand]
        private void NavigateToLibrary()
        {
            if (!CanNavigateAway()) return;

            CurrentPage = GetOrCreateView("Library", () => new LibraryPage { DataContext = LibraryViewModel });
            CurrentPageName = "Library";
            // Load from cache async - now properly optimized
            _ = LibraryViewModel.LoadFromCache();
        }

        [RelayCommand]
        private void NavigateToStore()
        {
            if (!CanNavigateAway()) return;

            CurrentPage = GetOrCreateView("Store", () => new StorePage { DataContext = StoreViewModel });
            CurrentPageName = "Store";
            // Check API key when navigating to Store
            StoreViewModel.OnNavigatedTo();
        }

        [RelayCommand]
        private void NavigateToDownloads()
        {
            if (!CanNavigateAway()) return;

            CurrentPage = GetOrCreateView("Downloads", () => new DownloadsPage { DataContext = DownloadsViewModel });
            CurrentPageName = "Downloads";
        }

        [RelayCommand]
        private void NavigateToWorkshop()
        {
            if (!CanNavigateAway()) return;

            CurrentPage = GetOrCreateView("Workshop", () => new WorkshopPage { DataContext = WorkshopViewModel });
            CurrentPageName = "Workshop";
        }

        [RelayCommand]
        private void NavigateToTools()
        {
            if (!CanNavigateAway()) return;

            CurrentPage = GetOrCreateView("Tools", () => new ToolsPage { DataContext = ToolsViewModel });
            CurrentPageName = "Tools";
        }

        [RelayCommand]
        private void NavigateToCloud()
        {
            if (!CanNavigateAway()) return;
            CurrentPage = GetOrCreateView("Cloud", () => new CloudPage { DataContext = this });
            CurrentPageName = "Cloud";
        }

        [RelayCommand]
        private void NavigateToSettings()
        {
            if (!CanNavigateAway()) return;

            // Settings page is always recreated to pick up layout changes (single-page toggle)
            _cachedViews.Remove("Settings");
            CurrentPage = GetOrCreateView("Settings", () => new SettingsPage { DataContext = SettingsViewModel });
            CurrentPageName = "Settings";
        }

        [RelayCommand]
        private void NavigateToSupport()
        {
            if (!CanNavigateAway()) return;

            CurrentPage = GetOrCreateView("Support", () => new SupportPage { DataContext = SupportViewModel });
            CurrentPageName = "Support";
        }

        [RelayCommand]
        private void MinimizeWindow(Window window)
        {
            window.WindowState = WindowState.Minimized;
        }

        [RelayCommand]
        private void MaximizeWindow(Window window)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        [RelayCommand]
        private void CloseWindow(Window window)
        {
            window.Close();
        }

        private void LoadSidebarVisibility()
        {
            var settings = _settingsService.LoadSettings();
            ShowWorkshop = settings.ShowWorkshopInSidebar;
            ShowCloudSaves = settings.ShowCloudSavesInSidebar;
            ShowTools = settings.ShowToolsInSidebar;
        }

        [RelayCommand]
        private void RestartSteam()
        {
            try
            {
                _steamService.RestartSteam();
                _notificationService.ShowSuccess("Steam is restarting...");
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to restart Steam: {ex.Message}");
            }
        }

    }
}
