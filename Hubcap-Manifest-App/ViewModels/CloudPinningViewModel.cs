using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HubcapManifestApp.Helpers;
using HubcapManifestApp.Services;
using HubcapManifestApp.Services.CloudRedirect;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace HubcapManifestApp.ViewModels
{
    public partial class CloudPinningViewModel : ObservableObject
    {
        private readonly NotificationService _notificationService;
        private readonly CacheService _cacheService;
        private readonly HashSet<uint> _pinnedApps = new();
        private bool _suppressAutoSave; // Prevent auto-save during initial load

        [ObservableProperty] private ObservableCollection<PinnedApp> _apps = new();
        [ObservableProperty] private bool _manifestPinningEnabled;
        [ObservableProperty] private bool _autoCommentEnabled;
        [ObservableProperty] private string _searchQuery = string.Empty;
        [ObservableProperty] private string _statusMessage = "Click Refresh to load lua apps";

        private static readonly Regex ManifestIdRegex = new(
            @"setManifestid\s*\(\s*(\d+)\s*,\s*""(\d+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public CloudPinningViewModel(NotificationService notificationService, CacheService cacheService)
        {
            _notificationService = notificationService;
            _cacheService = cacheService;
        }

        // Auto-save when toggles change (Fix #12)
        partial void OnManifestPinningEnabledChanged(bool value) { if (!_suppressAutoSave) SaveConfig(); }
        partial void OnAutoCommentEnabledChanged(bool value) { if (!_suppressAutoSave) SaveConfig(); }

        [RelayCommand]
        private async Task Refresh()
        {
            _suppressAutoSave = true;
            try
            {
                // Unsubscribe from old items
                foreach (var app in Apps)
                    app.PropertyChanged -= OnPinnedAppPropertyChanged;

                Apps.Clear();
                _pinnedApps.Clear();

            var steam = SteamDetector.FindSteamPath();
            if (steam == null) { StatusMessage = "Steam not found"; return; }

            // Load config
            var configPath = SteamDetector.GetPinConfigPath();
            if (configPath != null && File.Exists(configPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(configPath),
                        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                    var root = doc.RootElement;
                    if (root.TryGetProperty("manifest_pinning", out var mp) && mp.ValueKind == JsonValueKind.True)
                        ManifestPinningEnabled = true;
                    if (root.TryGetProperty("auto_comment", out var ac) && ac.ValueKind == JsonValueKind.True)
                        AutoCommentEnabled = true;
                    if (root.TryGetProperty("pinned_apps", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var el in arr.EnumerateArray())
                            if (el.TryGetUInt32(out var appId))
                                _pinnedApps.Add(appId);
                }
                catch { }
            }

            // Scan lua files
            var luaDir = Path.Combine(steam, SteamPaths.ConfigDir, SteamPaths.StPluginDir);
            if (!Directory.Exists(luaDir)) { StatusMessage = "No lua files found"; return; }

            var pinnedSnapshot = new HashSet<uint>(_pinnedApps);
            var apps = await Task.Run(() =>
            {
                var result = new List<PinnedApp>();
                foreach (var file in Directory.GetFiles(luaDir, "*.lua"))
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (!uint.TryParse(fileName, out var appId) || appId == 0) continue;

                    int depotCount = 0;
                    var lines = File.ReadAllLines(file);
                    foreach (var line in lines)
                    {
                        if (line.TrimStart().StartsWith("--")) continue;
                        if (ManifestIdRegex.IsMatch(line)) depotCount++;
                    }
                    if (depotCount == 0) continue;

                    result.Add(new PinnedApp
                    {
                        AppId = appId,
                        Name = $"App {appId}",
                        DepotCount = depotCount,
                        IsPinned = pinnedSnapshot.Contains(appId)
                    });
                }
                result.Sort((a, b) => a.AppId.CompareTo(b.AppId));
                return result;
            });

            foreach (var app in apps) Apps.Add(app);

            // Resolve names + header images async
            try
            {
                var ids = apps.Select(a => a.AppId).Distinct().ToList();
                var infos = await SteamStoreClient.Shared.GetAppInfoAsync(ids);
                foreach (var app in Apps)
                {
                    if (infos.TryGetValue(app.AppId, out var info))
                    {
                        if (!string.IsNullOrEmpty(info.Name))
                            app.Name = info.Name;
                        if (SteamStoreClient.IsValidSteamCdnUrl(info.HeaderUrl))
                            app.HeaderImageUrl = info.HeaderUrl;
                    }
                }
            }
            catch { }

            // Load header images in background
            _ = LoadHeaderImagesAsync();

            // Subscribe to pin changes for auto-save
            foreach (var app in Apps)
                app.PropertyChanged += OnPinnedAppPropertyChanged;

            StatusMessage = $"{Apps.Count} app(s) found";

            }
            finally
            {
                _suppressAutoSave = false;
            }
        }

        private async Task LoadHeaderImagesAsync()
        {
            foreach (var app in Apps.ToList())
            {
                try
                {
                    var appIdStr = app.AppId.ToString();
                    var cachedPath = await _cacheService.GetSteamGameIconAsync(
                        appIdStr,
                        localSteamIconPath: null,
                        cdnIconUrl: app.HeaderImageUrl ?? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appIdStr}/header.jpg");

                    if (!string.IsNullOrEmpty(cachedPath))
                        app.CachedIconPath = cachedPath;
                }
                catch { }
            }
        }

        private void OnPinnedAppPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PinnedApp.IsPinned) && !_suppressAutoSave)
                SaveConfig();
        }

        [RelayCommand]
        private void SaveConfig()
        {
            try
            {
                _pinnedApps.Clear();
                foreach (var app in Apps)
                    if (app.IsPinned) _pinnedApps.Add(app.AppId);

                var path = SteamDetector.GetPinConfigPath();
                if (path == null) return;

                // Only skip keys this page owns; cloud_redirect is preserved by ConfigHelper
                var ownedKeys = new[] { "manifest_pinning", "auto_comment", "pinned_apps" };

                var pinnedSnapshot = _pinnedApps.OrderBy(x => x).ToArray();
                var manifestPin = ManifestPinningEnabled;
                var autoComment = AutoCommentEnabled;

                ConfigHelper.SaveConfig(path, ownedKeys, writer =>
                {
                    writer.WriteBoolean("manifest_pinning", manifestPin);
                    writer.WriteBoolean("auto_comment", autoComment);
                    writer.WriteStartArray("pinned_apps");
                    foreach (var appId in pinnedSnapshot)
                        writer.WriteNumberValue(appId);
                    writer.WriteEndArray();
                });
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Save failed: {ex.Message}");
            }
        }
    }

    public class PinnedApp : ObservableObject
    {
        public uint AppId { get; set; }

        private string _name = "";
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public int DepotCount { get; set; }

        private bool _isPinned;
        public bool IsPinned
        {
            get => _isPinned;
            set => SetProperty(ref _isPinned, value);
        }

        public string? HeaderImageUrl { get; set; }

        private string? _cachedIconPath;
        public string? CachedIconPath
        {
            get => _cachedIconPath;
            set => SetProperty(ref _cachedIconPath, value);
        }

        public string DepotSummary => $"{DepotCount} depot{(DepotCount != 1 ? "s" : "")}";
    }
}
