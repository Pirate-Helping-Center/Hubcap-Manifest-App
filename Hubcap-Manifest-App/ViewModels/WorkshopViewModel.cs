using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HubcapManifestApp.Helpers;
using HubcapManifestApp.Services;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace HubcapManifestApp.ViewModels
{
    public partial class WorkshopViewModel : ObservableObject, IDisposable
    {
        private bool _disposed;
        private readonly WorkshopDownloadService _workshopService;
        private readonly SettingsService _settingsService;
        private readonly NotificationService _notificationService;
        private readonly DownloadHistoryService _downloadHistoryService;

        [ObservableProperty]
        private string _workshopIdsText = string.Empty;

        [ObservableProperty]
        private int _maxDownloads = 8;

        [ObservableProperty]
        private string _outputPath = string.Empty;

        [ObservableProperty]
        private bool _isDownloading;

        [ObservableProperty]
        private string _logText = string.Empty;

        [ObservableProperty]
        private string _statusMessage = "Enter Workshop IDs or URLs to download";

        public WorkshopViewModel(
            WorkshopDownloadService workshopService,
            SettingsService settingsService,
            NotificationService notificationService,
            DownloadHistoryService downloadHistoryService)
        {
            _workshopService = workshopService;
            _settingsService = settingsService;
            _notificationService = notificationService;
            _downloadHistoryService = downloadHistoryService;

            _workshopService.Log += OnLog;

            var settings = _settingsService.LoadSettings();
            OutputPath = string.IsNullOrEmpty(settings.DepotDownloaderOutputPath)
                ? System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    AppConstants.AppDataFolderName, "Workshop")
                : System.IO.Path.Combine(settings.DepotDownloaderOutputPath, "workshop");
        }

        private void OnLog(string msg)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                LogText += msg + "\n";
            });
        }

        private void AppendLog(string msg)
        {
            LogText += msg + "\n";
        }

        [RelayCommand]
        private async Task Download()
        {
            if (IsDownloading) return;

            var settings = _settingsService.LoadSettings();
            if (string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                _notificationService.ShowWarning("Please enter API key in settings");
                return;
            }

            var ids = WorkshopDownloadService.ParseWorkshopIds(WorkshopIdsText);
            if (ids.Count == 0)
            {
                _notificationService.ShowWarning("No valid Workshop IDs found in input");
                return;
            }

            IsDownloading = true;
            StatusMessage = $"Processing {ids.Count} item(s)...";
            AppendLog($"Queued {ids.Count} item(s): {string.Join(", ", ids)}");
            AppendLog($"  → Max downloads : {MaxDownloads}");
            AppendLog($"  → Output path   : {OutputPath}");

            int success = 0;
            int failed = 0;

            try
            {
                foreach (var wid in ids)
                {
                    AppendLog($"\n{'─', 0}────────────────────────────────────────────────────");
                    AppendLog($"  Workshop ID : {wid}");
                    StatusMessage = $"Processing {wid} ({success + failed + 1}/{ids.Count})...";

                    // 1. Resolve manifest
                    var item = await _workshopService.ResolveWorkshopItemAsync(wid, settings.ApiKey);
                    if (item == null)
                    {
                        failed++;
                        continue;
                    }

                    // 2. Check depot key from API header
                    if (string.IsNullOrEmpty(item.DepotKey))
                    {
                        AppendLog($"  ✗ No depot key available for App ID {item.AppId}. Skipping.");
                        failed++;
                        continue;
                    }
                    _workshopService.CacheDepotKey(item.AppId.ToString(), item.DepotKey);
                    AppendLog($"  ✓ Depot key received");

                    // 3. Download
                    var startTime = DateTime.Now;
                    var ok = await _workshopService.DownloadWorkshopItemAsync(item, OutputPath, MaxDownloads);
                    _downloadHistoryService.AddEntry(new Models.DownloadHistoryEntry
                    {
                        AppId = item.AppId.ToString(),
                        GameName = $"[Workshop] {item.Title}",
                        Status = ok ? "Completed" : "Failed",
                        StartTime = startTime,
                        EndTime = DateTime.Now,
                        DestinationPath = System.IO.Path.Combine(OutputPath, item.AppId.ToString(), item.WorkshopId),
                        Mode = "Workshop"
                    });
                    if (ok) success++;
                    else failed++;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"\n  ✗ Unexpected error: {ex.Message}");
            }
            finally
            {
                AppendLog($"\n{'─', 0}────────────────────────────────────────────────────");
                AppendLog($"  All tasks finished. {success} succeeded, {failed} failed.");
                IsDownloading = false;
                StatusMessage = $"Done — {success} succeeded, {failed} failed";
                _notificationService.ShowSuccess($"Workshop download complete: {success} succeeded, {failed} failed");
            }
        }

        [RelayCommand]
        private void BrowseOutput()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Workshop Output Folder"
            };

            if (dialog.ShowDialog() == true)
            {
                OutputPath = dialog.FolderName;
            }
        }

        [RelayCommand]
        private void ClearLog()
        {
            LogText = string.Empty;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _workshopService.Log -= OnLog;
        }
    }
}
