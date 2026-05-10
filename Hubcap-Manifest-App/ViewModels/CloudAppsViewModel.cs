using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HubcapManifestApp.Services;
using HubcapManifestApp.Services.CloudRedirect;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace HubcapManifestApp.ViewModels
{
    public partial class CloudAppsViewModel : ObservableObject
    {
        private readonly NotificationService _notificationService;
        private readonly CacheService _cacheService;

        [ObservableProperty] private ObservableCollection<CloudAppInfo> _apps = new();
        [ObservableProperty] private bool _isLoading;
        [ObservableProperty] private string _statusMessage = "Click Refresh to load syncing apps";
        [ObservableProperty] private string _searchQuery = string.Empty;

        private ICollectionView? _filteredApps;
        public ICollectionView FilteredApps =>
            _filteredApps ??= CreateFilteredView();

        public CloudAppsViewModel(NotificationService notificationService, CacheService cacheService)
        {
            _notificationService = notificationService;
            _cacheService = cacheService;
        }

        partial void OnSearchQueryChanged(string value) => _filteredApps?.Refresh();

        partial void OnAppsChanged(ObservableCollection<CloudAppInfo> value)
        {
            _filteredApps = CreateFilteredView();
            OnPropertyChanged(nameof(FilteredApps));
        }

        private ICollectionView CreateFilteredView()
        {
            var view = CollectionViewSource.GetDefaultView(Apps);
            view.Filter = obj =>
            {
                if (string.IsNullOrWhiteSpace(SearchQuery)) return true;
                if (obj is not CloudAppInfo app) return false;
                return app.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase)
                    || app.AppId.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase);
            };
            return view;
        }

        [RelayCommand]
        private async Task Refresh()
        {
            IsLoading = true;
            StatusMessage = "Loading...";
            Apps.Clear();

            try
            {
                var steam = SteamDetector.FindSteamPath();
                if (steam == null) { StatusMessage = "Steam not found"; IsLoading = false; return; }

                var storagePath = Path.Combine(steam, "cloud_redirect", "storage");
                if (!Directory.Exists(storagePath)) { StatusMessage = "No cloud storage found"; IsLoading = false; return; }

                var items = await Task.Run(() =>
                {
                    var result = new List<CloudAppInfo>();
                    try
                    {
                        foreach (var accountDir in Directory.GetDirectories(storagePath))
                        {
                            var accountId = Path.GetFileName(accountDir);
                            foreach (var appDir in Directory.GetDirectories(accountDir))
                            {
                                var appId = Path.GetFileName(appDir);
                                if (appId == "0") continue;
                                try
                                {
                                    var files = Directory.GetFiles(appDir, "*", SearchOption.AllDirectories);
                                    var totalSize = files.Sum(f =>
                                    {
                                        try { return new FileInfo(f).Length; }
                                        catch { return 0L; }
                                    });

                                    result.Add(new CloudAppInfo
                                    {
                                        AppId = appId,
                                        AccountId = accountId,
                                        Name = $"App {appId}",
                                        FileCount = files.Length,
                                        TotalSize = totalSize,
                                        SizeFormatted = FileUtils.FormatSize(totalSize)
                                    });
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                    return result;
                });

                Apps = new ObservableCollection<CloudAppInfo>(items);
                StatusMessage = $"{Apps.Count} app(s) syncing";

                // Resolve names and header image URLs from Steam store API
                try
                {
                    var ids = Apps
                        .Select(a => uint.TryParse(a.AppId, out var id) ? id : 0)
                        .Where(id => id > 0)
                        .Distinct()
                        .ToList();

                    var infos = await SteamStoreClient.Shared.GetAppInfoAsync(ids);
                    foreach (var app in Apps)
                    {
                        if (uint.TryParse(app.AppId, out var numericId) &&
                            infos.TryGetValue(numericId, out var info))
                        {
                            if (!string.IsNullOrEmpty(info.Name))
                                app.Name = info.Name;
                            if (SteamStoreClient.IsValidSteamCdnUrl(info.HeaderUrl))
                                app.HeaderImageUrl = info.HeaderUrl;
                        }
                    }
                }
                catch { }

                // Load header images in background (fire-and-forget per app)
                _ = LoadHeaderImagesAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task LoadHeaderImagesAsync()
        {
            foreach (var app in Apps.ToList())
            {
                try
                {
                    var cachedPath = await _cacheService.GetSteamGameIconAsync(
                        app.AppId,
                        localSteamIconPath: null,
                        cdnIconUrl: app.HeaderImageUrl ?? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{app.AppId}/header.jpg");

                    if (!string.IsNullOrEmpty(cachedPath))
                        app.CachedIconPath = cachedPath;
                }
                catch { }
            }
        }

        [RelayCommand]
        private async Task DeleteApp(CloudAppInfo? app)
        {
            if (app == null) return;

            var confirmed = await Dialog.ConfirmDangerAsync(
                "Delete Cloud Saves",
                $"Permanently delete all cloud saves for {app.Name} ({app.AppId})?\n\n" +
                $"Files: {app.FileCount}\n" +
                $"Size: {app.SizeFormatted}\n\n" +
                "This will delete saves from the local cloud_redirect storage AND the configured cloud provider (if any). This cannot be undone.");

            if (!confirmed) return;

            IsLoading = true;
            StatusMessage = $"Deleting {app.Name}...";

            int localDeleted = 0;
            try
            {
                var steam = SteamDetector.FindSteamPath();

                // 1. Back up local files FIRST — ensures recoverability before any destructive ops
                if (steam != null)
                {
                    var localPath = Path.Combine(steam, "cloud_redirect", "storage", app.AccountId, app.AppId);
                    if (Directory.Exists(localPath))
                    {
                        var backupRoot = BackupPaths.GetAppDeleteRoot(steam);
                        var backupDir = Path.Combine(backupRoot, app.AccountId,
                            $"{app.AppId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}");

                        // If backup fails, abort the entire delete — don't risk data loss
                        Directory.CreateDirectory(backupDir);

                        var files = Directory.GetFiles(localPath, "*", SearchOption.AllDirectories);
                        foreach (var file in files)
                        {
                            var relativePath = Path.GetRelativePath(localPath, file);
                            // Guard against symlink escape — skip files outside the app directory
                            if (relativePath.StartsWith("..") || Path.IsPathRooted(relativePath))
                                continue;
                            var destPath = Path.Combine(backupDir, relativePath);
                            var destDir = Path.GetDirectoryName(destPath);
                            if (!string.IsNullOrEmpty(destDir))
                                Directory.CreateDirectory(destDir);
                            File.Move(file, destPath);
                            localDeleted++;
                        }

                        // Save undo log
                        var undoLog = new UndoLog
                        {
                            Timestamp = DateTime.UtcNow.ToString("o"),
                            Operations = files.Select(f => new UndoOperation
                            {
                                Type = "file_move",
                                SourcePath = f,
                                DestPath = Path.Combine(backupDir, Path.GetRelativePath(localPath, f)),
                                AppId = uint.TryParse(app.AppId, out var id) ? id : 0
                            }).ToList()
                        };
                        var logJson = System.Text.Json.JsonSerializer.Serialize(undoLog, CleanupJsonContext.Default.UndoLog);
                        FileUtils.AtomicWriteAllText(Path.Combine(backupDir, "undo_log.json"), logJson);

                        // Remove the directory only if it's truly empty after backup
                        try { Directory.Delete(localPath, false); }
                        catch (IOException) { } // not empty — leave it
                    }
                }

                // 2. Delete from cloud provider (safe — local backup already secured)
                using var client = new CloudProviderClient();
                var cloudResult = await client.DeleteAppDataAsync(app.AccountId, app.AppId);

                // Remove from list
                Apps.Remove(app);
                StatusMessage = $"{Apps.Count} app(s) syncing";

                var summary = $"Deleted {app.Name}";
                if (localDeleted > 0)
                    summary += $" — {localDeleted} local file(s) backed up";
                if (cloudResult.FilesDeleted > 0)
                    summary += $", {cloudResult.FilesDeleted} cloud file(s) removed";
                if (!string.IsNullOrEmpty(cloudResult.Error))
                    summary += $"\nNote: {cloudResult.Error}";

                if (cloudResult.Success)
                    _notificationService.ShowSuccess(summary);
                else
                    _notificationService.ShowWarning(summary);
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Delete failed: {ex.Message}" +
                    (localDeleted > 0 ? "\nLocal files were backed up safely and can be restored." : ""));
                StatusMessage = $"Delete failed: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    public class CloudAppInfo : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public string AppId { get; set; } = "";
        public string AccountId { get; set; } = "";

        private string _name = "";
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        private string? _cachedIconPath;
        public string? CachedIconPath
        {
            get => _cachedIconPath;
            set => SetProperty(ref _cachedIconPath, value);
        }

        /// <summary>Header image URL from Steam store API (validated CDN URL).</summary>
        public string? HeaderImageUrl { get; set; }

        public int FileCount { get; set; }
        public long TotalSize { get; set; }
        public string SizeFormatted { get; set; } = "";
    }
}
