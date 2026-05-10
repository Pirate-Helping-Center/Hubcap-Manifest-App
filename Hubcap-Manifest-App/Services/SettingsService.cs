using HubcapManifestApp.Helpers;
using HubcapManifestApp.Interfaces;
using HubcapManifestApp.Models;
using Newtonsoft.Json;
using System;
using System.IO;

namespace HubcapManifestApp.Services
{
    public class SettingsService : ISettingsService
    {
        private readonly string _settingsPath;
        private AppSettings? _settings;

        public event EventHandler<AppSettings>? SettingsChanged;

        public SettingsService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appFolder = Path.Combine(appData, AppConstants.AppDataFolderName);
            Directory.CreateDirectory(appFolder);
            _settingsPath = Path.Combine(appFolder, "settings.json");
        }

        public AppSettings LoadSettings()
        {
            if (_settings != null)
                return _settings;

            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    _settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
                else
                {
                    _settings = new AppSettings();
                    SaveSettings(_settings);
                }
            }
            catch
            {
                _settings = new AppSettings();
            }

            // Set default downloads path if empty
            if (string.IsNullOrEmpty(_settings.DownloadsPath))
            {
                _settings.DownloadsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    AppConstants.AppDataFolderName,
                    "Downloads"
                );
            }

            // Ensure downloads directory exists
            try
            {
                if (!Directory.Exists(_settings.DownloadsPath))
                {
                    Directory.CreateDirectory(_settings.DownloadsPath);
                }
            }
            catch
            {
                // If we can't create in Documents, fall back to AppData
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var fallbackPath = Path.Combine(appData, AppConstants.AppDataFolderName, "Downloads");
                Directory.CreateDirectory(fallbackPath);
                _settings.DownloadsPath = fallbackPath;
            }

            return _settings;
        }

        public void SaveSettings(AppSettings settings)
        {
            try
            {
                _settings = settings;

                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);

                // Write with explicit flush to disk
                using (var fileStream = new FileStream(_settingsPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(fileStream))
                {
                    writer.Write(json);
                    writer.Flush();
                    fileStream.Flush(flushToDisk: true);
                }

                SettingsChanged?.Invoke(this, settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save settings: {ex.Message}", ex);
            }
        }

        public void AddApiKeyToHistory(string apiKey)
        {
            var settings = LoadSettings();
            if (!settings.ApiKeyHistory.Contains(apiKey))
            {
                settings.ApiKeyHistory.Add(apiKey);
                if (settings.ApiKeyHistory.Count > 10)
                {
                    settings.ApiKeyHistory.RemoveAt(0);
                }
                SaveSettings(settings);
            }
        }

    }
}
