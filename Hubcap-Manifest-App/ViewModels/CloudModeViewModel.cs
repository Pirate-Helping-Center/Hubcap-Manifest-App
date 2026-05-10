using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HubcapManifestApp.Helpers;
using HubcapManifestApp.Services;
using HubcapManifestApp.Services.CloudRedirect;
using System;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace HubcapManifestApp.ViewModels
{
    public partial class CloudModeViewModel : ObservableObject
    {
        private readonly NotificationService _notificationService;

        [ObservableProperty] private string _currentMode = "Not set";
        [ObservableProperty] private bool _isCloudRedirectMode;
        [ObservableProperty] private bool _isStFixerMode;

        public CloudModeViewModel(NotificationService notificationService)
        {
            _notificationService = notificationService;
        }

        [RelayCommand]
        private void Refresh()
        {
            var mode = SteamDetector.ReadModeSetting();
            if (mode == "cloud_redirect")
            {
                CurrentMode = "CloudRedirect";
                IsCloudRedirectMode = true;
                IsStFixerMode = false;
            }
            else if (mode == "stfixer")
            {
                CurrentMode = "STFixer";
                IsCloudRedirectMode = false;
                IsStFixerMode = true;
            }
            else
            {
                CurrentMode = "Not configured";
                IsCloudRedirectMode = false;
                IsStFixerMode = false;
            }
        }

        [RelayCommand]
        private void SelectCloudRedirect()
        {
            var result = MessageBoxHelper.Show(
                "CloudRedirect mode redirects Steam Cloud saves to your chosen cloud provider.\n\n" +
                "This is experimental software. Back up your saves before proceeding.\n\nContinue?",
                "Enable CloudRedirect", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            if (!SaveModeSetting("cloud_redirect") || !SetDllCloudRedirect(true))
            {
                _notificationService.ShowError("Failed to save mode configuration");
                return;
            }
            Refresh();
            _notificationService.ShowSuccess("CloudRedirect mode enabled. Restart Steam to apply.");
        }

        [RelayCommand]
        private void SelectStFixer()
        {
            if (!SaveModeSetting("stfixer") || !SetDllCloudRedirect(false))
            {
                _notificationService.ShowError("Failed to save mode configuration");
                return;
            }
            Refresh();
            _notificationService.ShowSuccess("STFixer mode enabled. Restart Steam to apply.");
        }

        private static bool SaveModeSetting(string mode)
        {
            try
            {
                var path = Path.Combine(SteamDetector.GetConfigDir(), "settings.json");
                ConfigHelper.SaveConfig(path, new[] { "mode" }, writer =>
                {
                    writer.WriteString("mode", mode);
                });
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save mode setting: {ex.Message}");
                return false;
            }
        }

        private static bool SetDllCloudRedirect(bool enabled)
        {
            try
            {
                var path = SteamDetector.GetPinConfigPath();
                if (path == null) return false;

                ConfigHelper.SaveConfig(path, new[] { "cloud_redirect" }, writer =>
                {
                    writer.WriteBoolean("cloud_redirect", enabled);
                });
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save DLL config: {ex.Message}");
                return false;
            }
        }
    }
}
