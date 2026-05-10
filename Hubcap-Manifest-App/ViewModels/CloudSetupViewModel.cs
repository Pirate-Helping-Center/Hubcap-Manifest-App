using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HubcapManifestApp.Helpers;
using HubcapManifestApp.Services;
using HubcapManifestApp.Services.CloudRedirect;
using HubcapManifestApp.Services.CloudRedirect.Patching;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace HubcapManifestApp.ViewModels
{
    public partial class CloudSetupViewModel : ObservableObject
    {
        private readonly NotificationService _notificationService;

        [ObservableProperty] private string _logText = string.Empty;
        [ObservableProperty] private bool _isRunning;
        [ObservableProperty] private string _patchStatus = "Not checked";
        [ObservableProperty] private string _offlineStatus = "Not checked";
        [ObservableProperty] private string _steamToolsStatus = "Not checked";
        [ObservableProperty] private string _dllStatus = "Not checked";

        // Steam version check (Fix #2)
        [ObservableProperty] private string _steamVersionStatus = "Not checked";
        [ObservableProperty] private bool _isVersionMismatch;

        // Steam path display + browse (Fix #3)
        [ObservableProperty] private string _steamPathDisplay = "Detecting...";
        [ObservableProperty] private bool _isSteamMissing;

        public CloudSetupViewModel(NotificationService notificationService)
        {
            _notificationService = notificationService;
        }

        /// <summary>
        /// Let the user manually pick the Steam installation folder when auto-detection fails.
        /// </summary>
        [RelayCommand]
        private async Task BrowseSteamPath()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Steam Installation Folder"
            };
            if (dialog.ShowDialog() != true) return;

            var path = dialog.FolderName;

            // Basic validation: steam.exe should exist
            if (!File.Exists(Path.Combine(path, SteamPaths.SteamExe)))
            {
                MessageBoxHelper.Show(
                    $"'{path}' doesn't appear to be a Steam installation (steam.exe not found).",
                    "Invalid Path", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SteamDetector.SetSteamPath(path);
            await RefreshStatus();
        }

        private void Log(string msg)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => LogText += msg + "\n");
        }

        [RelayCommand]
        private async Task RunAllPatches()
        {
            if (IsRunning) return;
            IsRunning = true;
            LogText = string.Empty;

            try
            {
                await Task.Run(async () =>
                {
                    var steamPath = SteamDetector.FindSteamPath();
                    if (steamPath == null)
                    {
                        Log("✗ Steam not found.");
                        return;
                    }

                    Log($"Steam path: {steamPath}");

                    // Ensure Steam is closed
                    Log("→ Checking if Steam is running...");
                    if (SteamDetector.IsSteamRunning())
                    {
                        // Must show dialog on UI thread
                        var dialogResult = Application.Current?.Dispatcher.Invoke(() =>
                            MessageBoxHelper.Show(
                                "Steam must be closed to apply patches. Close Steam now?",
                                "Close Steam", MessageBoxButton.YesNo, MessageBoxImage.Question));
                        if (dialogResult != MessageBoxResult.Yes) { Log("Cancelled."); return; }

                        Log("→ Waiting for Steam to close...");

                        bool exited = await SteamDetector.ShutdownAndWaitAsync(steamPath);
                        if (!exited)
                        {
                            Log("✗ Steam didn't shut down in time. Please close it manually and try again.");
                            _notificationService.ShowWarning("Steam didn't shut down in time.");
                            return;
                        }

                        Log("✓ Steam closed");
                    }

                    var patcher = new Patcher(steamPath, msg => Log($"  {msg}"));
                    int failures = 0;

                    // Pre-step A: Ensure SteamTools core DLLs are present (xinput1_4.dll / dwmapi.dll)
                    if (!patcher.HasCoreDll())
                    {
                        Log("\n→ Downloading SteamTools core DLLs...");
                        var repairResult = patcher.RepairCoreDlls();
                        if (!repairResult.Succeeded)
                        {
                            Log($"✗ Core DLL download failed: {repairResult.Error}");
                            _notificationService.ShowWarning("Could not download required DLLs. Aborting.");
                            return;
                        }
                        Log("✓ Core DLLs ready");
                    }

                    // Pre-step B: Fetch cloud_redirect.dll from GitHub releases (cached locally)
                    if (!EmbeddedDll.IsAvailable())
                    {
                        Log("\n→ Downloading cloud_redirect.dll from GitHub...");
                        var fetchErr = await EmbeddedDll.FetchLatestAsync(msg => Log($"  {msg}"));
                        if (fetchErr != null)
                        {
                            Log($"✗ Failed to download DLL: {fetchErr}");
                            failures++;
                        }
                        else
                            Log("✓ DLL downloaded and cached");
                    }

                    // Step 1: Offline setup (patches hijack DLL + encrypted payload cache)
                    Log("\n[1/4] Applying offline setup...");
                    try
                    {
                        var r = patcher.ApplyOfflineSetup();
                        if (r.Succeeded)
                            Log("✓ Offline setup applied");
                        else
                        { Log($"✗ Offline setup failed: {r.Error}"); failures++; }
                    }
                    catch (Exception ex) { Log($"✗ Offline setup failed: {ex.Message}"); failures++; }

                    // Step 2: Patch SteamTools.exe (prevents overwrite of core DLL patches)
                    Log("\n[2/4] Patching SteamTools.exe...");
                    try
                    {
                        var ok = patcher.PatchSteamToolsExe();
                        if (ok)
                            Log("✓ SteamTools.exe patched");
                        else
                        {
                            // "Not found" is not a failure — SteamTools may not be installed
                            var stState = patcher.GetSteamToolsExePatchState();
                            if (stState == -1)
                                Log("⊘ SteamTools.exe not found (skipped — not installed)");
                            else
                            { Log("✗ SteamTools.exe patch failed"); failures++; }
                        }
                    }
                    catch (Exception ex) { Log($"✗ SteamTools.exe patch failed: {ex.Message}"); failures++; }

                    // Step 3: Apply CloudRedirect namespace (injects hook + deploys DLL)
                    Log("\n[3/4] Applying CloudRedirect patch...");
                    try
                    {
                        var r = patcher.ApplyCloudRedirectNamespace();
                        if (r.Succeeded)
                            Log("✓ CloudRedirect patch applied");
                        else
                        { Log($"✗ CloudRedirect patch failed: {r.Error}"); failures++; }
                    }
                    catch (Exception ex) { Log($"✗ CloudRedirect patch failed: {ex.Message}"); failures++; }

                    // Step 4: Deploy cloud_redirect.dll
                    Log("\n[4/4] Deploying cloud_redirect.dll...");
                    var destPath = Path.Combine(steamPath, "cloud_redirect.dll");
                    var deployErr = EmbeddedDll.DeployTo(destPath);
                    if (deployErr != null)
                    { Log($"✗ DLL deploy failed: {deployErr}"); failures++; }
                    else
                        Log("✓ DLL deployed");

                    Log("\n────────────────────────────────────────────");
                    if (failures == 0)
                    {
                        Log("All patches complete. Start Steam to activate.");
                        _notificationService.ShowSuccess("CloudRedirect patches applied successfully");
                    }
                    else
                    {
                        Log($"Finished with {failures} failure(s). Review the log above.");
                        _notificationService.ShowWarning($"Patching finished with {failures} failure(s)");
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"\n✗ Unexpected error: {ex.Message}");
            }
            finally
            {
                IsRunning = false;
                await RefreshStatus();
            }
        }

        [RelayCommand]
        private async Task RefreshStatus()
        {
            await Task.Run(async () =>
            {
                var steam = SteamDetector.FindSteamPath();

                if (steam == null)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        SteamPathDisplay = "Not found";
                        IsSteamMissing = true;
                        PatchStatus = "Steam not found";
                        OfflineStatus = "N/A";
                        SteamToolsStatus = "N/A";
                        DllStatus = "N/A";
                        SteamVersionStatus = "N/A";
                        IsVersionMismatch = true;
                    });
                    return;
                }

                // Version check (Fix #2)
                var version = SteamDetector.GetSteamVersion(steam);
                string versionLabel;
                bool mismatch;
                if (version == null)
                {
                    versionLabel = "Unknown (manifest not found)";
                    mismatch = true;
                }
                else if (version.Value == SteamDetector.ExpectedSteamVersion)
                {
                    versionLabel = $"{version.Value} (supported)";
                    mismatch = false;
                }
                else
                {
                    versionLabel = $"{version.Value} (expected {SteamDetector.ExpectedSteamVersion})";
                    mismatch = true;
                }

                var patcher = new Patcher(steam);
                var coreState = patcher.GetPatchState();
                var offlineState = patcher.GetOfflinePatchState();
                var stState = patcher.GetSteamToolsExePatchState();
                var dllPath = Path.Combine(steam, "cloud_redirect.dll");
                var dllExists = File.Exists(dllPath);

                string stLabel = stState switch
                {
                    0 => "Patched",
                    1 => "Unpatched",
                    _ => "Not found"
                };

                // Compare deployed DLL against latest GitHub release
                string dllLabel;
                if (!dllExists)
                    dllLabel = "Not deployed";
                else
                {
                    var current = await EmbeddedDll.IsDeployedCurrentRemoteAsync(dllPath);
                    if (current == true) dllLabel = "Current";
                    else if (current == false) dllLabel = "Outdated";
                    else dllLabel = "Deployed (unable to verify)";
                }

                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    SteamPathDisplay = steam;
                    IsSteamMissing = false;
                    SteamVersionStatus = versionLabel;
                    IsVersionMismatch = mismatch;
                    PatchStatus = coreState.ToString();
                    OfflineStatus = offlineState.ToString();
                    SteamToolsStatus = stLabel;
                    DllStatus = dllLabel;
                });
            });
        }

        [RelayCommand]
        private void ClearLog() => LogText = string.Empty;
    }
}
