using Newtonsoft.Json;
using HubcapManifestApp.Helpers;
using HubcapManifestApp.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HubcapManifestApp.Services
{
    public class DownloadHistoryService
    {
        private readonly string _historyPath;
        private readonly object _lock = new();
        private List<DownloadHistoryEntry> _entries = new();
        private const int MaxEntries = 500;

        private readonly SettingsService _settingsService;

        public DownloadHistoryService(DownloadService downloadService, SettingsService settingsService)
        {
            _settingsService = settingsService;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _historyPath = Path.Combine(appData, AppConstants.AppDataFolderName, "download_history.json");

            Load();

            downloadService.DownloadCompleted += (_, item) =>
            {
                AddEntry(new DownloadHistoryEntry
                {
                    AppId = item.AppId,
                    GameName = item.GameName,
                    TotalBytes = item.TotalBytes,
                    Status = "Completed",
                    StatusMessage = item.StatusMessage,
                    StartTime = item.StartTime,
                    EndTime = item.EndTime ?? DateTime.Now,
                    DestinationPath = item.DestinationPath,
                    Mode = GetDownloadMode(item)
                });
            };

            downloadService.DownloadFailed += (_, item) =>
            {
                AddEntry(new DownloadHistoryEntry
                {
                    AppId = item.AppId,
                    GameName = item.GameName,
                    TotalBytes = item.TotalBytes,
                    Status = "Failed",
                    StatusMessage = item.StatusMessage,
                    StartTime = item.StartTime,
                    EndTime = item.EndTime ?? DateTime.Now,
                    DestinationPath = item.DestinationPath,
                    Mode = GetDownloadMode(item)
                });
            };
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_historyPath))
                {
                    var json = File.ReadAllText(_historyPath);
                    _entries = JsonConvert.DeserializeObject<List<DownloadHistoryEntry>>(json) ?? new();
                }
            }
            catch
            {
                _entries = new();
            }
        }

        public void AddEntry(DownloadHistoryEntry entry)
        {
            lock (_lock)
            {
                _entries.Insert(0, entry);
                if (_entries.Count > MaxEntries)
                    _entries = _entries.Take(MaxEntries).ToList();
                Save();
            }
        }

        public IReadOnlyList<DownloadHistoryEntry> GetHistory()
        {
            lock (_lock)
            {
                return _entries.AsReadOnly();
            }
        }

        public void ClearHistory()
        {
            lock (_lock)
            {
                _entries.Clear();
                Save();
            }
        }

        private string GetDownloadMode(Models.DownloadItem item)
        {
            if (item.IsDepotDownloaderMode)
                return "DepotDownloader";

            // Check settings to determine if this was a DepotDownloader manifest fetch
            try
            {
                var settings = _settingsService.LoadSettings();
                if (settings.Mode == Models.ToolMode.DepotDownloader)
                    return "DepotDownloader (Manifest)";
            }
            catch { }

            return "SteamTools";
        }

        private void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_historyPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_historyPath, JsonConvert.SerializeObject(_entries, Formatting.Indented));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save download history: {ex.Message}");
            }
        }
    }
}
