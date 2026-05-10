using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HubcapManifestApp.Helpers;
using HubcapManifestApp.Services;
using HubcapManifestApp.Services.CloudRedirect;
using HubcapManifestApp.Services.CloudRedirect.Patching;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace HubcapManifestApp.ViewModels
{
    public partial class CloudDashboardViewModel : ObservableObject
    {
        private readonly SteamService _steamService;
        private readonly NotificationService _notificationService;

        [ObservableProperty] private string _steamPath = "Detecting...";
        [ObservableProperty] private string _dllStatus = "Checking...";
        [ObservableProperty] private string _providerStatus = "Checking...";
        [ObservableProperty] private string _providerDetail = "";
        [ObservableProperty] private string _appCount = "0";
        [ObservableProperty] private bool _dllNeedsUpdate;
        [ObservableProperty] private bool _isLoading;

        // Patch states displayed in the dashboard
        [ObservableProperty] private string _corePatchState = "Not checked";
        [ObservableProperty] private string _offlinePatchState = "Not checked";
        [ObservableProperty] private string _steamToolsPatchState = "Not checked";
        [ObservableProperty] private bool _isAnyPatched;

        public CloudDashboardViewModel(SteamService steamService, NotificationService notificationService)
        {
            _steamService = steamService;
            _notificationService = notificationService;
        }

        [RelayCommand]
        private async Task Refresh()
        {
            IsLoading = true;
            try
            {
                await Task.Run(async () =>
                {
                    var steam = SteamDetector.FindSteamPath();
                    Application.Current?.Dispatcher.Invoke(() => SteamPath = steam ?? "Not found");

                    if (steam == null) return;

                    var dllPath = Path.Combine(steam, "cloud_redirect.dll");
                    var exists = File.Exists(dllPath);

                    // Determine DLL status
                    string status;
                    bool needsUpdate;

                    if (!exists)
                    {
                        status = "Not installed";
                        needsUpdate = false;
                    }
                    else
                    {
                        // Compare deployed DLL hash against the latest GitHub release
                        var current = await EmbeddedDll.IsDeployedCurrentRemoteAsync(dllPath);
                        if (current == true) { status = "Installed & current"; needsUpdate = false; }
                        else if (current == false) { status = "Update available"; needsUpdate = true; }
                        else { status = "Installed (unable to check for updates)"; needsUpdate = false; }
                    }

                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        DllStatus = status;
                        DllNeedsUpdate = needsUpdate;
                    });

                    var config = SteamDetector.ReadConfig();
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        if (config == null)
                        {
                            ProviderStatus = "Not configured";
                            ProviderDetail = "No config file found";
                        }
                        else
                        {
                            ProviderStatus = config.DisplayName;

                            if (config.IsOAuth && !string.IsNullOrEmpty(config.TokenPath))
                            {
                                var status = OAuthService.CheckTokenStatus(config.TokenPath);
                                ProviderDetail = status.IsAuthenticated
                                    ? "Authenticated"
                                    : status.Message;
                            }
                            else if (config.IsFolder)
                            {
                                ProviderDetail = !string.IsNullOrEmpty(config.SyncPath)
                                    ? (Directory.Exists(config.SyncPath)
                                        ? $"Folder: {config.SyncPath}"
                                        : $"Folder not found: {config.SyncPath}")
                                    : "No sync path configured";
                            }
                            else if (config.IsLocal)
                            {
                                ProviderDetail = "Saves stored locally only";
                            }
                            else
                            {
                                ProviderDetail = "";
                            }
                        }
                    });

                    int count = 0;
                    var storagePath = Path.Combine(steam, "cloud_redirect", "storage");
                    if (Directory.Exists(storagePath))
                    {
                        foreach (var accountDir in Directory.GetDirectories(storagePath))
                            count += Directory.GetDirectories(accountDir).Length;
                    }
                    Application.Current?.Dispatcher.Invoke(() => AppCount = count.ToString());

                    // Patch states
                    var patcher = new Patcher(steam);
                    var coreState = patcher.GetPatchState();
                    var offlineState = patcher.GetOfflinePatchState();
                    var stState = patcher.GetSteamToolsExePatchState();

                    string stLabel = stState switch
                    {
                        0 => "Patched",
                        1 => "Unpatched",
                        _ => "Not found"
                    };

                    bool anyPatched = coreState == PatchState.Patched
                                   || coreState == PatchState.PartiallyPatched
                                   || coreState == PatchState.OutOfDate
                                   || offlineState == PatchState.Patched
                                   || offlineState == PatchState.PartiallyPatched
                                   || stState == 0;

                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        CorePatchState = coreState.ToString();
                        OfflinePatchState = offlineState.ToString();
                        SteamToolsPatchState = stLabel;
                        IsAnyPatched = anyPatched;
                    });
                });
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private void OpenLog()
        {
            var steam = SteamDetector.FindSteamPath();
            if (steam == null) return;
            var logPath = Path.Combine(steam, "cloud_redirect.log");
            if (File.Exists(logPath))
                Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
            else
                _notificationService.ShowWarning("Log file not found");
        }

        [RelayCommand]
        private async Task RestartSteam()
        {
            try
            {
                var steamPath = SteamDetector.FindSteamPath();
                if (steamPath == null) return;
                var steamExe = Path.Combine(steamPath, SteamPaths.SteamExe);
                if (!File.Exists(steamExe)) return;

                _notificationService.ShowSuccess("Shutting down Steam...");

                bool exited = await SteamDetector.ShutdownAndWaitAsync(steamPath);
                if (!exited)
                {
                    _notificationService.ShowWarning("Steam didn't shut down in time. Please restart it manually.");
                    return;
                }

                // Small grace period after process exits
                await Task.Delay(1000);

                // Verify Steam is still not running before relaunching
                if (SteamDetector.IsSteamRunning())
                {
                    _notificationService.ShowWarning("Steam is already running.");
                    return;
                }

                Process.Start(new ProcessStartInfo(steamExe) { UseShellExecute = true })?.Dispose();
                _notificationService.ShowSuccess("Steam restarting...");
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task UpdateDll()
        {
            var steam = SteamDetector.FindSteamPath();
            if (steam == null) return;

            // Steam must be closed to replace the DLL
            if (SteamDetector.IsSteamRunning())
            {
                var close = MessageBoxHelper.Show(
                    "Steam must be closed to update the DLL. Close Steam now?",
                    "Close Steam", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (close != MessageBoxResult.Yes) return;

                bool exited = await SteamDetector.ShutdownAndWaitAsync(steam);
                if (!exited)
                {
                    _notificationService.ShowWarning("Steam didn't shut down in time. Please close it manually and try again.");
                    return;
                }
            }

            var dest = Path.Combine(steam, "cloud_redirect.dll");
            var err = await EmbeddedDll.FetchAndDeployAsync(dest);
            if (err != null)
                _notificationService.ShowError($"Deploy failed: {err}");
            else
            {
                _notificationService.ShowSuccess("DLL updated. Restart Steam to apply.");
                DllStatus = "Installed & current";
                DllNeedsUpdate = false;
            }
        }

        [RelayCommand]
        private async Task RevertAllPatches()
        {
            var steamPath = SteamDetector.FindSteamPath();
            if (steamPath == null)
            {
                _notificationService.ShowError("Steam not found.");
                return;
            }

            var confirm = MessageBoxHelper.Show(
                "This will revert all CloudRedirect patches and remove the DLL. Continue?",
                "Revert Patches", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            // Steam must be closed to revert patches
            if (SteamDetector.IsSteamRunning())
            {
                var close = MessageBoxHelper.Show(
                    "Steam must be closed to revert patches. Close Steam now?",
                    "Close Steam", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (close != MessageBoxResult.Yes) return;

                bool exited = await SteamDetector.ShutdownAndWaitAsync(steamPath);
                if (!exited)
                {
                    _notificationService.ShowWarning("Steam didn't shut down in time. Please close it manually and try again.");
                    return;
                }
            }

            IsLoading = true;
            try
            {
                int failures = 0;

                await Task.Run(() =>
                {
                    var patcher = new Patcher(steamPath);

                    // Revert CloudRedirect namespace (removes hook + DLL)
                    try
                    {
                        var r = patcher.RevertCloudRedirectNamespace();
                        if (!r.Succeeded)
                        { Debug.WriteLine($"Revert CloudRedirect failed: {r.Error}"); failures++; }
                    }
                    catch (Exception ex) { Debug.WriteLine($"Revert CloudRedirect error: {ex.Message}"); failures++; }

                    // Unpatch SteamTools.exe
                    try
                    {
                        if (!patcher.UnpatchSteamToolsExe())
                        { Debug.WriteLine("Unpatch SteamTools.exe returned false"); failures++; }
                    }
                    catch (Exception ex) { Debug.WriteLine($"Unpatch SteamTools error: {ex.Message}"); failures++; }

                    // Revert offline setup
                    try
                    {
                        var r = patcher.RevertOfflineSetup();
                        if (!r.Succeeded)
                        { Debug.WriteLine($"Revert offline setup failed: {r.Error}"); failures++; }
                    }
                    catch (Exception ex) { Debug.WriteLine($"Revert offline setup error: {ex.Message}"); failures++; }
                });

                if (failures == 0)
                    _notificationService.ShowSuccess("All patches reverted successfully.");
                else
                    _notificationService.ShowWarning($"Revert finished with {failures} failure(s). Some patches may remain.");
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Revert failed: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
                await RefreshCommand.ExecuteAsync(null);
            }
        }
    }
}
