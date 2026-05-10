using HubcapManifestApp.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace HubcapManifestApp.Services
{
    public class InstalledManifestInfo
    {
        public string AppId { get; set; } = "";
        public string GameName { get; set; } = "";
        public ulong ManifestId { get; set; }
        public DateTime InstalledDate { get; set; }
        public string InstallPath { get; set; } = "";
        public List<uint> DepotIds { get; set; } = new();
    }

    public class ManifestStorageService
    {
        private readonly LoggerService _logger;
        private readonly string _manifestFolder;
        private readonly string _indexFilePath;
        private Dictionary<string, InstalledManifestInfo> _installedManifests = new();

        public string ManifestFolder => _manifestFolder;

        public ManifestStorageService(LoggerService logger)
        {
            _logger = logger;
            _manifestFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppConstants.AppDataFolderName,
                "Manifests"
            );
            _indexFilePath = Path.Combine(_manifestFolder, "manifest_index.json");

            EnsureDirectoryExists();
            LoadIndex();
        }

        private void EnsureDirectoryExists()
        {
            if (!Directory.Exists(_manifestFolder))
            {
                Directory.CreateDirectory(_manifestFolder);
                _logger.Debug($"Created manifest storage folder: {_manifestFolder}");
            }
        }

        private void LoadIndex()
        {
            try
            {
                if (File.Exists(_indexFilePath))
                {
                    var json = File.ReadAllText(_indexFilePath);
                    _installedManifests = JsonSerializer.Deserialize<Dictionary<string, InstalledManifestInfo>>(json) ?? new();
                    _logger.Debug($"Loaded {_installedManifests.Count} manifest entries from index");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load manifest index: {ex.Message}");
                _installedManifests = new();
            }
        }

        private void SaveIndex()
        {
            try
            {
                var json = JsonSerializer.Serialize(_installedManifests, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_indexFilePath, json);
                _logger.Debug("Saved manifest index");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save manifest index: {ex.Message}");
            }
        }

        // Marker folder name written into each game install directory.
        // Visible (not hidden) so users see it and understand it's metadata they shouldn't delete.
        //
        // Rebrand dual-support: new installs write to <see cref="MarkerFolderName"/> only,
        // but readers fall back to <see cref="LegacyMarkerFolderName"/> so users who installed
        // under pre-rebrand Solus builds don't lose their install-detection / update / uninstall
        // state after upgrading to Hubcap. The legacy folder is never written by this build; it
        // is only read. If both folders exist in the same game directory (e.g. the user reinstalled
        // on top of a legacy install), the new folder wins — see <see cref="TryReadInstallMarker"/>.
        public const string MarkerFolderName = ".hubcapmanifestapp";
        public const string LegacyMarkerFolderName = ".solusmanifestapp";
        public const string MarkerFileName = "install.json";

        public void StoreManifest(string appId, string gameName, ulong manifestId, string installPath, List<uint>? depotIds = null)
        {
            var info = new InstalledManifestInfo
            {
                AppId = appId,
                GameName = gameName,
                ManifestId = manifestId,
                InstalledDate = DateTime.Now,
                InstallPath = installPath,
                DepotIds = depotIds ?? new()
            };

            // Write the per-install marker first (source of truth, survives cache wipe / AppData wipe)
            TryWriteInstallMarker(info);

            // Then update the in-memory cache + on-disk index
            _installedManifests[appId] = info;
            SaveIndex();

            _logger.Info($"Stored manifest info for {gameName} (AppId: {appId}, ManifestId: {manifestId})");
        }

        /// <summary>
        /// Writes {installPath}/.hubcapmanifestapp/install.json with the provided info.
        /// Best-effort: failures are logged but not thrown so cache writes still proceed.
        /// Only the current (non-legacy) marker folder is written; legacy <c>.solusmanifestapp</c>
        /// folders from pre-rebrand installs are read-only and intentionally left untouched.
        /// </summary>
        public bool TryWriteInstallMarker(InstalledManifestInfo info)
        {
            if (string.IsNullOrEmpty(info.InstallPath))
            {
                _logger.Debug($"Skipping install marker for {info.AppId}: no install path");
                return false;
            }

            try
            {
                if (!Directory.Exists(info.InstallPath))
                {
                    _logger.Debug($"Install path does not exist, skipping marker: {info.InstallPath}");
                    return false;
                }

                var markerDir = Path.Combine(info.InstallPath, MarkerFolderName);
                Directory.CreateDirectory(markerDir);

                var markerPath = Path.Combine(markerDir, MarkerFileName);
                var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(markerPath, json);

                _logger.Debug($"Wrote install marker: {markerPath}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to write install marker for {info.AppId} at {info.InstallPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reads an install marker from a candidate game directory. Returns null if missing or invalid.
        /// Prefers the current <see cref="MarkerFolderName"/>; falls back to the legacy
        /// <see cref="LegacyMarkerFolderName"/> for installs created by pre-rebrand Solus builds.
        /// </summary>
        public InstalledManifestInfo? TryReadInstallMarker(string gameDirectory)
        {
            // Probe new then legacy. Deliberate priority: if a game dir contains both
            // (reinstall on top of a Solus-era install), the fresh marker is authoritative
            // because it reflects the most recent install action.
            var marker = TryReadInstallMarkerFrom(gameDirectory, MarkerFolderName);
            if (marker != null)
                return marker;

            return TryReadInstallMarkerFrom(gameDirectory, LegacyMarkerFolderName);
        }

        private InstalledManifestInfo? TryReadInstallMarkerFrom(string gameDirectory, string markerFolder)
        {
            try
            {
                var markerPath = Path.Combine(gameDirectory, markerFolder, MarkerFileName);
                if (!File.Exists(markerPath))
                    return null;

                var json = File.ReadAllText(markerPath);
                return JsonSerializer.Deserialize<InstalledManifestInfo>(json);
            }
            catch (Exception ex)
            {
                _logger.Debug($"Failed to read install marker from {gameDirectory}\\{markerFolder}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Scans the given root directory (one level deep, then two levels deep to handle the
        /// "{GameName} ({AppId})/{GameName}" nested layout) for install markers matching the AppId.
        /// Returns the first match.
        /// </summary>
        public InstalledManifestInfo? ScanForInstallMarker(string rootDirectory, string appId)
        {
            if (string.IsNullOrEmpty(rootDirectory) || !Directory.Exists(rootDirectory))
                return null;

            try
            {
                // Look up to 2 levels deep — the fresh-install layout is {root}/{Name (AppId)}/{Name}/
                foreach (var lvl1 in Directory.EnumerateDirectories(rootDirectory))
                {
                    var marker = TryReadInstallMarker(lvl1);
                    if (marker != null && marker.AppId == appId)
                        return marker;

                    try
                    {
                        foreach (var lvl2 in Directory.EnumerateDirectories(lvl1))
                        {
                            marker = TryReadInstallMarker(lvl2);
                            if (marker != null && marker.AppId == appId)
                                return marker;
                        }
                    }
                    catch { /* ignore unreadable subdirs */ }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"ScanForInstallMarker failed at {rootDirectory}: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Cache-first lookup with marker-scan fallback. If the cache entry's install path
        /// no longer contains a marker, treat the cache as stale and drop it.
        /// </summary>
        public InstalledManifestInfo? GetInstalledManifestWithFallback(string appId, IEnumerable<string> scanRoots)
        {
            // 1. Cache hit
            if (_installedManifests.TryGetValue(appId, out var cached))
            {
                // Verify the marker still exists at the recorded path; if not, the install was moved/deleted.
                if (!string.IsNullOrEmpty(cached.InstallPath))
                {
                    // Accept either marker folder as proof-of-install. Pre-rebrand Solus installs
                    // only have the legacy folder; we must not treat those as "stale" just because
                    // the new folder is missing.
                    var newMarkerPath = Path.Combine(cached.InstallPath, MarkerFolderName, MarkerFileName);
                    var legacyMarkerPath = Path.Combine(cached.InstallPath, LegacyMarkerFolderName, MarkerFileName);
                    if (File.Exists(newMarkerPath) || File.Exists(legacyMarkerPath))
                        return cached;

                    _logger.Debug($"Cache entry for {appId} is stale (no marker at {cached.InstallPath}), falling through to scan");
                    _installedManifests.Remove(appId);
                    SaveIndex();
                }
                else
                {
                    // Legacy entry with no install path — return it as-is (caller will handle)
                    return cached;
                }
            }

            // 2. Scan configured roots for a matching marker
            foreach (var root in scanRoots)
            {
                var found = ScanForInstallMarker(root, appId);
                if (found != null)
                {
                    _logger.Info($"Adopted install marker for {appId} found at {found.InstallPath}");
                    _installedManifests[appId] = found;
                    SaveIndex();
                    return found;
                }
            }

            return null;
        }

        public void StoreManifestFile(string appId, uint depotId, ulong manifestId, byte[] manifestData)
        {
            try
            {
                var fileName = $"{appId}_{depotId}_{manifestId}.manifest";
                var filePath = Path.Combine(_manifestFolder, fileName);
                File.WriteAllBytes(filePath, manifestData);
                _logger.Debug($"Stored manifest file: {fileName}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to store manifest file: {ex.Message}");
            }
        }

        public byte[]? GetManifestFile(string appId, uint depotId, ulong manifestId)
        {
            try
            {
                var fileName = $"{appId}_{depotId}_{manifestId}.manifest";
                var filePath = Path.Combine(_manifestFolder, fileName);
                if (File.Exists(filePath))
                {
                    return File.ReadAllBytes(filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to read manifest file: {ex.Message}");
            }
            return null;
        }

        public InstalledManifestInfo? GetInstalledManifest(string appId)
        {
            return _installedManifests.TryGetValue(appId, out var info) ? info : null;
        }

        public bool IsInstalled(string appId)
        {
            return _installedManifests.ContainsKey(appId);
        }

        public ulong? GetInstalledManifestId(string appId)
        {
            return _installedManifests.TryGetValue(appId, out var info) ? info.ManifestId : null;
        }

        public bool HasUpdate(string appId, ulong latestManifestId)
        {
            var installed = GetInstalledManifestId(appId);
            if (installed == null)
                return false;

            return installed.Value != latestManifestId;
        }

        public void RemoveManifest(string appId)
        {
            if (_installedManifests.Remove(appId))
            {
                SaveIndex();
                _logger.Debug($"Removed manifest info for AppId: {appId}");
            }
        }

        public IEnumerable<InstalledManifestInfo> GetAllInstalledManifests()
        {
            return _installedManifests.Values;
        }

    }
}
