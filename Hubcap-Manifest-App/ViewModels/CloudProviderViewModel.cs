using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HubcapManifestApp.Services;
using HubcapManifestApp.Services.CloudRedirect;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace HubcapManifestApp.ViewModels
{
    public partial class CloudProviderViewModel : ObservableObject
    {
        private readonly NotificationService _notificationService;
        private CancellationTokenSource? _authCts;
        private OAuthService? _oauth;

        [ObservableProperty] private string _selectedProvider = "local";
        [ObservableProperty] private string _tokenPath = string.Empty;
        [ObservableProperty] private string _syncPath = string.Empty;
        [ObservableProperty] private string _authStatus = "Not configured";
        [ObservableProperty] private bool _isAuthenticating;
        [ObservableProperty] private bool _showPathInput;
        [ObservableProperty] private bool _showSignIn;
        [ObservableProperty] private string _logText = string.Empty;

        public CloudProviderViewModel(NotificationService notificationService)
        {
            _notificationService = notificationService;
            LoadConfig();
        }

        private void LoadConfig()
        {
            var config = SteamDetector.ReadConfig();
            if (config != null)
            {
                // The DLL uses "folder" for both local-only and folder-sync modes.
                // Distinguish them: if provider is "folder" but no sync path is set,
                // the user selected "Local Only".
                var provider = config.Provider ?? "local";
                if (provider == "folder" && string.IsNullOrEmpty(config.SyncPath))
                    provider = "local";

                SelectedProvider = provider;
                TokenPath = config.TokenPath ?? "";
                SyncPath = config.SyncPath ?? "";

                if (config.IsOAuth && !string.IsNullOrEmpty(config.TokenPath))
                {
                    var status = OAuthService.CheckTokenStatus(config.TokenPath);
                    AuthStatus = status.IsAuthenticated
                        ? $"Authenticated ({config.DisplayName})"
                        : status.Message;
                }
                else
                {
                    AuthStatus = config.DisplayName ?? "Not configured";
                }
            }
            UpdateProviderUI();
        }

        partial void OnSelectedProviderChanged(string value) => UpdateProviderUI();

        private void UpdateProviderUI()
        {
            ShowSignIn = SelectedProvider == "gdrive" || SelectedProvider == "onedrive";
            ShowPathInput = SelectedProvider == "folder";
        }

        [RelayCommand]
        private async Task SignIn()
        {
            if (IsAuthenticating) return;
            IsAuthenticating = true;
            LogText = string.Empty;
            _authCts = new CancellationTokenSource();

            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var configDir = Path.Combine(appData, "CloudRedirect");
                Directory.CreateDirectory(configDir);

                var tokenFile = SelectedProvider == "gdrive"
                    ? Path.Combine(configDir, "google_tokens.json")
                    : Path.Combine(configDir, "onedrive_tokens.json");

                _oauth = new OAuthService();
                var success = await _oauth.AuthorizeAsync(
                    SelectedProvider, tokenFile,
                    msg => Application.Current?.Dispatcher.Invoke(() => LogText += msg + "\n"),
                    _authCts.Token);

                if (success)
                {
                    TokenPath = tokenFile;
                    SaveConfig();
                    AuthStatus = $"Authenticated ({SelectedProvider})";
                    _notificationService.ShowSuccess("Cloud provider authenticated!");
                }
                else
                {
                    AuthStatus = "Authentication failed";
                    _notificationService.ShowError("Authentication failed or was cancelled");
                }
            }
            catch (OperationCanceledException)
            {
                AuthStatus = "Authentication cancelled";
            }
            catch (Exception ex)
            {
                AuthStatus = $"Error: {ex.Message}";
            }
            finally
            {
                IsAuthenticating = false;
                _oauth?.Dispose();
                _oauth = null;
            }
        }

        [RelayCommand]
        private void CancelAuth()
        {
            _authCts?.Cancel();
        }

        [RelayCommand]
        private void SaveConfig()
        {
            var configPath = SteamDetector.GetConfigFilePath();
            ConfigHelper.SaveConfig(configPath, new[] { "provider", "token_path", "sync_path" }, writer =>
            {
                var provider = SelectedProvider == "local" ? "folder" : SelectedProvider;
                writer.WriteString("provider", provider);
                if (!string.IsNullOrEmpty(TokenPath))
                    writer.WriteString("token_path", TokenPath);
                if (!string.IsNullOrEmpty(SyncPath))
                    writer.WriteString("sync_path", SyncPath);
            });

            // Ensure the DLL-side pin config exists with at least the defaults (Fix #13).
            // Without this file the DLL doesn't know which provider to use.
            EnsureDllConfig();

            _notificationService.ShowSuccess("Cloud provider configuration saved");
        }

        /// <summary>
        /// If the DLL-side config (<Steam>/cloud_redirect/config.json) doesn't exist yet,
        /// write a minimal default so the DLL activates on next Steam launch.
        /// For the "local" provider, also create the <Steam>/localcloud directory.
        /// </summary>
        private void EnsureDllConfig()
        {
            try
            {
                var pinConfigPath = SteamDetector.GetPinConfigPath();
                if (pinConfigPath == null) return; // Steam not found
                if (File.Exists(pinConfigPath)) return; // Already has a config

                var steam = SteamDetector.FindSteamPath();
                if (steam == null) return;

                var pinDir = Path.GetDirectoryName(pinConfigPath)!;
                Directory.CreateDirectory(pinDir);

                // For local provider, default sync path is <steam>/localcloud
                var isLocal = SelectedProvider == "local" || (SelectedProvider == "folder" && string.IsNullOrEmpty(SyncPath));
                var defaultSyncPath = isLocal ? Path.Combine(steam, "localcloud") : SyncPath;
                if (isLocal && !string.IsNullOrEmpty(defaultSyncPath))
                    Directory.CreateDirectory(defaultSyncPath);

                ConfigHelper.SaveConfig(pinConfigPath, new[] { "cloud_redirect" }, writer =>
                {
                    writer.WriteBoolean("cloud_redirect", true);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EnsureDllConfig failed: {ex.Message}");
            }
        }

        [RelayCommand]
        private void BrowsePath()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Cloud Sync Folder"
            };
            if (dialog.ShowDialog() == true)
                SyncPath = dialog.FolderName;
        }
    }
}
