using Newtonsoft.Json;
using HubcapManifestApp.Helpers;
using HubcapManifestApp.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HubcapManifestApp.Services
{
    public class WorkshopDownloadService
    {
        private readonly ManifestApiService _manifestApiService;
        private readonly DepotDownloaderWrapperService _depotDownloader;
        private readonly LoggerService _logger;
        private readonly string _keyCachePath;
        private Dictionary<string, string> _keyCache = new();

        public event Action<string>? Log;

        public WorkshopDownloadService(ManifestApiService manifestApiService, DepotDownloaderWrapperService depotDownloader, LoggerService logger)
        {
            _manifestApiService = manifestApiService;
            _depotDownloader = depotDownloader;
            _logger = logger;

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _keyCachePath = Path.Combine(appData, AppConstants.AppDataFolderName, "workshop_keys.json");
            LoadKeyCache();
        }

        private void LoadKeyCache()
        {
            try
            {
                if (File.Exists(_keyCachePath))
                {
                    var json = File.ReadAllText(_keyCachePath);
                    _keyCache = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new();
                }
            }
            catch
            {
                _keyCache = new();
            }
        }

        private void SaveKeyCache()
        {
            try
            {
                var dir = Path.GetDirectoryName(_keyCachePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_keyCachePath, JsonConvert.SerializeObject(_keyCache, Formatting.Indented));
            }
            catch { }
        }

        /// <summary>
        /// Resolves a workshop item by downloading its manifest and reading response headers.
        /// </summary>
        public async Task<WorkshopItem?> ResolveWorkshopItemAsync(string workshopId, string apiKey)
        {
            LogMsg($"  → Fetching workshop manifest for {workshopId}...");

            var result = await _manifestApiService.GetWorkshopManifestAsync(workshopId, apiKey);
            if (result == null)
            {
                LogMsg($"  ✗ Could not download manifest for workshop item {workshopId}");
                return null;
            }

            var (manifestData, item) = result.Value;

            // Save manifest file
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var manifestDir = Path.Combine(appData, AppConstants.AppDataFolderName, "Manifests");
            Directory.CreateDirectory(manifestDir);
            var manifestPath = Path.Combine(manifestDir, $"{item.DepotId}_{item.ManifestId}.manifest");
            await File.WriteAllBytesAsync(manifestPath, manifestData);
            item.ManifestFilePath = manifestPath;

            LogMsg($"  ✓ App ID     : {item.AppId}");
            LogMsg($"  ✓ Depot ID   : {item.DepotId}");
            LogMsg($"  ✓ Manifest   : {item.ManifestId}");
            LogMsg($"  ✓ Title      : {item.Title}");

            return item;
        }

        /// <summary>
        /// Gets the depot key for an app, using local cache first, then fetching from lua API.
        /// </summary>
        public async Task<string?> GetDepotKeyAsync(string appId, string apiKey)
        {
            // Check local cache first
            if (_keyCache.TryGetValue(appId, out var cachedKey))
            {
                LogMsg($"  ✓ Depot key for {appId} loaded from cache");
                return cachedKey;
            }

            LogMsg($"  → Fetching depot key via Lua API for App ID {appId}...");
            var luaContent = await _manifestApiService.GetLuaContentAsync(appId, apiKey);
            if (string.IsNullOrEmpty(luaContent))
            {
                LogMsg($"  ✗ Could not fetch lua for App ID {appId}");
                return null;
            }

            // Parse depot key from lua — look for MAIN APPLICATION section, then addappid line
            var key = ExtractDepotKeyFromLua(luaContent);
            if (string.IsNullOrEmpty(key))
            {
                LogMsg($"  ✗ Could not extract depot key from lua for App ID {appId}");
                return null;
            }

            // Cache it
            _keyCache[appId] = key;
            SaveKeyCache();
            LogMsg($"  ✓ Key cached : {appId} → {key[..Math.Min(10, key.Length)]}…");

            return key;
        }

        private static string? ExtractDepotKeyFromLua(string luaContent)
        {
            bool inMain = false;
            foreach (var line in luaContent.Split('\n'))
            {
                var stripped = line.Trim();
                if (stripped.Contains("-- MAIN APPLICATION"))
                {
                    inMain = true;
                    continue;
                }
                if (inMain && stripped.StartsWith("addappid("))
                {
                    var parts = stripped.Split('"');
                    if (parts.Length >= 3)
                        return parts[1];
                }
            }
            return null;
        }

        /// <summary>
        /// Downloads a resolved workshop item using DepotDownloader.
        /// </summary>
        public async Task<bool> DownloadWorkshopItemAsync(
            WorkshopItem item, string outputPath, int maxDownloads = 8,
            CancellationToken cancellationToken = default)
        {
            var targetDir = Path.Combine(outputPath, item.AppId.ToString(), item.WorkshopId);
            Directory.CreateDirectory(targetDir);

            LogMsg($"  → Connecting to Steam (anonymous)...");
            var initialized = await _depotDownloader.InitializeAsync("", "");
            if (!initialized)
            {
                LogMsg($"  ✗ Failed to connect to Steam. Check your internet connection.");
                return false;
            }

            LogMsg($"  → Downloading to {targetDir}...");

            // Subscribe to DepotDownloader events for verbose output
            EventHandler<DownloadProgressEventArgs>? progressHandler = null;
            EventHandler<LogMessageEventArgs>? logHandler = null;

            progressHandler = (s, e) =>
            {
                LogMsg($"    [{e.Progress:F0}%] {e.CurrentFile} ({e.ProcessedFiles}/{e.TotalFiles} files)");
            };
            logHandler = (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Message) && !e.Message.Contains("[OwnerResolve]"))
                    LogMsg($"    {e.Message}");
            };

            _depotDownloader.ProgressChanged += progressHandler;
            _depotDownloader.LogMessage += logHandler;

            var depots = new List<(uint depotId, string depotKey, string? manifestFile, uint ownerAppId)>
            {
                (item.DepotId, item.DepotKey, item.ManifestFilePath, item.AppId)
            };

            // Store the depot key so DepotDownloader can find it
            try { DepotDownloader.DepotKeyStore.AddKey($"{item.DepotId};{item.DepotKey}"); } catch { }

            try
            {
                var success = await _depotDownloader.DownloadDepotsAsync(
                    item.AppId,
                    depots,
                    targetDir,
                    verifyFiles: true,
                    maxDownloads: maxDownloads,
                    isUgc: true,
                    cancellationToken: cancellationToken);

                if (success)
                    LogMsg($"  ✓ Download complete → {targetDir}");
                else
                    LogMsg($"  ✗ Download failed for workshop item {item.WorkshopId}");

                return success;
            }
            catch (Exception ex)
            {
                LogMsg($"  ✗ Download error: {ex.Message}");
                return false;
            }
            finally
            {
                _depotDownloader.ProgressChanged -= progressHandler;
                _depotDownloader.LogMessage -= logHandler;
            }
        }

        /// <summary>
        /// Parses workshop IDs from user input (URLs, raw IDs, comma/newline separated).
        /// </summary>
        public static List<string> ParseWorkshopIds(string input)
        {
            var ids = new List<string>();
            var seen = new HashSet<string>();

            foreach (var token in Regex.Split(input, @"[\s,]+"))
            {
                var t = token.Trim();
                if (string.IsNullOrEmpty(t)) continue;

                string? wid = null;
                var urlMatch = Regex.Match(t, @"[?&]id=(\d+)");
                if (urlMatch.Success)
                    wid = urlMatch.Groups[1].Value;
                else if (Regex.IsMatch(t, @"^\d+$"))
                    wid = t;

                if (wid != null && seen.Add(wid))
                    ids.Add(wid);
            }

            return ids;
        }

        public void CacheDepotKey(string appId, string key)
        {
            if (!_keyCache.ContainsKey(appId))
            {
                _keyCache[appId] = key;
                SaveKeyCache();
            }
        }

        private void LogMsg(string msg)
        {
            _logger.Info(msg);
            Log?.Invoke(msg);
        }
    }
}
