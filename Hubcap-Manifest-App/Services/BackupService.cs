using HubcapManifestApp.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HubcapManifestApp.Services
{
    public class BackupData
    {
        public DateTime BackupDate { get; set; }
        public string Version { get; set; } = "1.0";
        public AppSettings Settings { get; set; } = new AppSettings();
        public List<string> InstalledModAppIds { get; set; } = new List<string>();
        public List<Manifest> GameMetadata { get; set; } = new List<Manifest>();
    }

    public class BackupService
    {
        private readonly FileInstallService _fileInstallService;
        private readonly SettingsService _settingsService;
        private readonly CacheService _cacheService;

        public BackupService(
            FileInstallService fileInstallService,
            SettingsService settingsService,
            CacheService cacheService)
        {
            _fileInstallService = fileInstallService;
            _settingsService = settingsService;
            _cacheService = cacheService;
        }

        public async Task<string> CreateBackupAsync(string backupPath)
        {
            try
            {
                var backup = new BackupData
                {
                    BackupDate = DateTime.Now,
                    Settings = _settingsService.LoadSettings()
                };

                // Get installed mods
                var installedGames = await Task.Run(() => _fileInstallService.GetInstalledGames());
                backup.InstalledModAppIds = installedGames.Select(g => g.AppId).ToList();

                // Try to get cached metadata
                foreach (var appId in backup.InstalledModAppIds)
                {
                    var manifest = _cacheService.GetCachedManifest(appId);
                    if (manifest != null)
                    {
                        backup.GameMetadata.Add(manifest);
                    }
                }

                // Serialize to JSON
                var json = JsonConvert.SerializeObject(backup, Formatting.Indented);

                // Save to file
                var fileName = $"HubcapBackup_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                var fullPath = Path.Combine(backupPath, fileName);

                await File.WriteAllTextAsync(fullPath, json);

                return fullPath;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to create backup: {ex.Message}", ex);
            }
        }

        public async Task<BackupData> LoadBackupAsync(string backupFilePath)
        {
            try
            {
                if (!File.Exists(backupFilePath))
                {
                    throw new FileNotFoundException("Backup file not found");
                }

                var json = await File.ReadAllTextAsync(backupFilePath);
                var backup = JsonConvert.DeserializeObject<BackupData>(json);

                if (backup == null)
                {
                    throw new Exception("Invalid backup file format");
                }

                return backup;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to load backup: {ex.Message}", ex);
            }
        }

        public Task<RestoreResult> RestoreBackupAsync(BackupData backup, bool restoreSettings = true)
        {
            var result = new RestoreResult();

            try
            {
                // Restore settings if requested
                if (restoreSettings && backup.Settings != null)
                {
                    _settingsService.SaveSettings(backup.Settings);
                    result.SettingsRestored = true;
                }

                // Cache the metadata for later reference
                foreach (var manifest in backup.GameMetadata)
                {
                    _cacheService.CacheManifest(manifest);
                }

                result.Success = true;
                result.TotalMods = backup.InstalledModAppIds.Count;
                result.Message = $"Backup loaded successfully. {backup.InstalledModAppIds.Count} mods need to be downloaded and installed.";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Restore failed: {ex.Message}";
            }

            return Task.FromResult(result);
        }

    }

    public class RestoreResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public bool SettingsRestored { get; set; }
        public int TotalMods { get; set; }
        public int RestoredMods { get; set; }
    }
}
