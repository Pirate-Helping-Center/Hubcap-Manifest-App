using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HubcapManifestApp.Services;
using HubcapManifestApp.Services.CloudRedirect;
using System;
using System.IO;
using System.Text.Json;

namespace HubcapManifestApp.ViewModels
{
    public partial class CloudExperimentalViewModel : ObservableObject
    {
        private readonly NotificationService _notificationService;
        private bool _loading; // Suppress auto-save during initial load

        [ObservableProperty] private bool _syncAchievements;
        [ObservableProperty] private bool _syncPlaytime;
        [ObservableProperty] private bool _syncLuas;
        [ObservableProperty] private bool _isCloudRedirectMode;
        [ObservableProperty] private string _statusMessage = "";

        public CloudExperimentalViewModel(NotificationService notificationService)
        {
            _notificationService = notificationService;
        }

        partial void OnSyncAchievementsChanged(bool value) => AutoSave();
        partial void OnSyncPlaytimeChanged(bool value) => AutoSave();
        partial void OnSyncLuasChanged(bool value) => AutoSave();

        [RelayCommand]
        private void Refresh()
        {
            _loading = true;
            try
            {
                // Check if we're in CloudRedirect mode
                IsCloudRedirectMode = SteamDetector.ReadModeSetting() == "cloud_redirect";

                if (!IsCloudRedirectMode)
                {
                    StatusMessage = "Sync settings are only available in CloudRedirect mode.";
                    SyncAchievements = false;
                    SyncPlaytime = false;
                    SyncLuas = false;
                    return;
                }

                // Load toggle states from config.json
                var configPath = SteamDetector.GetConfigFilePath();
                if (!File.Exists(configPath))
                {
                    StatusMessage = "Config file not found. Save your provider settings first.";
                    SyncAchievements = false;
                    SyncPlaytime = false;
                    SyncLuas = false;
                    return;
                }

                try
                {
                    var json = File.ReadAllText(configPath);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    SyncAchievements = root.TryGetProperty("sync_achievements", out var a) && a.ValueKind == JsonValueKind.True;
                    SyncPlaytime = root.TryGetProperty("sync_playtime", out var p) && p.ValueKind == JsonValueKind.True;
                    SyncLuas = root.TryGetProperty("sync_luas", out var l) && l.ValueKind == JsonValueKind.True;

                    StatusMessage = "Changes take effect after restarting Steam.";
                }
                catch
                {
                    StatusMessage = "Failed to read config file.";
                }
            }
            finally
            {
                _loading = false;
            }
        }

        private void AutoSave()
        {
            if (_loading) return;
            SaveSyncToggles();
        }

        private void SaveSyncToggles()
        {
            try
            {
                var configPath = SteamDetector.GetConfigFilePath();
                ConfigHelper.SaveConfig(configPath,
                    new[] { "sync_achievements", "sync_playtime", "sync_luas" },
                    writer =>
                    {
                        writer.WriteBoolean("sync_achievements", SyncAchievements);
                        writer.WriteBoolean("sync_playtime", SyncPlaytime);
                        writer.WriteBoolean("sync_luas", SyncLuas);
                    });
                StatusMessage = "Saved. Restart Steam to apply changes.";
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to save sync settings: {ex.Message}");
            }
        }
    }
}
