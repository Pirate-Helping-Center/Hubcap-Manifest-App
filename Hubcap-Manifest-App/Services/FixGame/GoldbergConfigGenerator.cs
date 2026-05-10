using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace HubcapManifestApp.Services.FixGame
{
    /// <summary>
    /// Generates Goldberg Steam Emulator steam_settings/ folder.
    /// Achievements/stats fetched fresh from Steam Web API.
    /// DLC list from cached lua data.
    /// </summary>
    public class GoldbergConfigGenerator
    {
        private readonly FixGameCacheService _cache;
        private readonly LoggerService _logger;

        public event Action<string>? Log;

        public GoldbergConfigGenerator(FixGameCacheService cache)
        {
            _cache = cache;
            _logger = new LoggerService("GoldbergConfig");
        }

        /// <summary>
        /// Generates the steam_settings folder in the target directory.
        /// </summary>
        public async Task<bool> GenerateAsync(
            string appId,
            string targetDir,
            string? steamWebApiKey,
            string language = "english",
            string steamId = "76561198001737783",
            string playerName = "Player")
        {
            var settingsDir = Path.Combine(targetDir, "steam_settings");
            Directory.CreateDirectory(settingsDir);

            try
            {
                // 1. steam_appid.txt
                File.WriteAllText(Path.Combine(targetDir, "steam_appid.txt"), appId);
                LogMsg($"  Created steam_appid.txt");

                // 2. DLC list — goes into configs.app.ini [app::dlcs] section
                var appInfo = _cache.LoadAppInfo(appId);
                var dlcEntries = appInfo?.DlcList;
                if (dlcEntries == null || dlcEntries.Count == 0)
                {
                    dlcEntries = await FetchDlcFromStoreAsync(appId);
                    if (dlcEntries.Count > 0)
                        LogMsg($"  DLC: {dlcEntries.Count} entries (from Store API)");
                }
                else
                {
                    LogMsg($"  DLC: {dlcEntries.Count} entries (from cache)");
                }

                // 3. configs.app.ini (with DLC and cloud save dirs)
                var appIni = $"[app::general]\n" +
                             $"build_id=0\n" +
                             $"steam_id_remote_storage={steamId}\n";

                // Add DLC section
                if (dlcEntries != null && dlcEntries.Count > 0)
                {
                    appIni += "\n[app::dlcs]\n";
                    appIni += "unlock_all=0\n";
                    foreach (var dlc in dlcEntries)
                        appIni += $"{dlc.AppId}={dlc.Name}\n";
                }

                // Add cloud save dirs from cached PICS data
                var cloudSaveJson = _cache.LoadPicsJson(appId + "_cloudsave");
                if (!string.IsNullOrEmpty(cloudSaveJson))
                {
                    try
                    {
                        var cloudData = JObject.Parse(cloudSaveJson);
                        var winDirs = ResolveCloudSaveDirs("Windows", cloudData);
                        var linuxDirs = ResolveCloudSaveDirs("Linux", cloudData);

                        if (winDirs.Count > 0)
                        {
                            appIni += "\n[app::cloud_save::win]\n";
                            for (int i = 0; i < winDirs.Count; i++)
                                appIni += $"dir{i + 1}={winDirs[i]}\n";
                            LogMsg($"  Cloud save dirs (Windows): {winDirs.Count}");
                        }

                        if (linuxDirs.Count > 0)
                        {
                            appIni += "\n[app::cloud_save::linux]\n";
                            for (int i = 0; i < linuxDirs.Count; i++)
                                appIni += $"dir{i + 1}={linuxDirs[i]}\n";
                            LogMsg($"  Cloud save dirs (Linux): {linuxDirs.Count}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogMsg($"  Could not parse cloud save dirs: {ex.Message}");
                    }
                }

                File.WriteAllText(Path.Combine(settingsDir, "configs.app.ini"), appIni);
                LogMsg($"  Created configs.app.ini");

                // 4. configs.main.ini
                var mainIni = "[main::general]\n" +
                              $"account_name={playerName}\n" +
                              $"account_steamid={steamId}\n" +
                              $"language={language}\n";
                File.WriteAllText(Path.Combine(settingsDir, "configs.main.ini"), mainIni);
                LogMsg($"  Created configs.main.ini");

                // 5. Achievements + Stats (requires Steam Web API key)
                if (!string.IsNullOrEmpty(steamWebApiKey))
                {
                    await FetchAndWriteAchievementsAsync(appId, settingsDir, steamWebApiKey);
                }
                else
                {
                    LogMsg("  Skipping achievements/stats (no Steam Web API key)");
                }

                LogMsg("  Config generation complete");
                return true;
            }
            catch (Exception ex)
            {
                LogMsg($"  Config generation failed: {ex.Message}");
                _logger.Error($"Config generation error: {ex}");
                return false;
            }
        }

        private async Task FetchAndWriteAchievementsAsync(string appId, string settingsDir, string apiKey)
        {
            try
            {
                LogMsg("  Fetching achievements and stats...");

                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                var url = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?appid={appId}&key={apiKey}";
                var response = await client.GetStringAsync(url);
                var data = JObject.Parse(response);

                var gameStats = data["game"]?["availableGameStats"];
                if (gameStats == null)
                {
                    LogMsg("  No achievement/stat data available for this game");
                    return;
                }

                // Achievements
                var achievements = gameStats["achievements"] as JArray;
                if (achievements != null && achievements.Count > 0)
                {
                    var achList = new List<object>();
                    foreach (var ach in achievements)
                    {
                        achList.Add(new
                        {
                            name = ach["name"]?.ToString() ?? "",
                            displayName = ach["displayName"]?.ToString() ?? "",
                            description = ach["description"]?.ToString() ?? "",
                            icon = ach["icon"]?.ToString() ?? "",
                            icongray = ach["icongray"]?.ToString() ?? "",
                            hidden = ach["hidden"]?.Value<int>() ?? 0
                        });
                    }

                    File.WriteAllText(
                        Path.Combine(settingsDir, "achievements.json"),
                        JsonConvert.SerializeObject(achList, Formatting.Indented));
                    LogMsg($"  achievements.json: {achList.Count} achievements");
                }

                // Stats
                var stats = gameStats["stats"] as JArray;
                if (stats != null && stats.Count > 0)
                {
                    var statDict = new Dictionary<string, object>();
                    foreach (var stat in stats)
                    {
                        var name = stat["name"]?.ToString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            statDict[name] = new
                            {
                                type = "int",
                                @default = stat["defaultvalue"]?.Value<int>() ?? 0
                            };
                        }
                    }

                    File.WriteAllText(
                        Path.Combine(settingsDir, "stats.json"),
                        JsonConvert.SerializeObject(statDict, Formatting.Indented));
                    LogMsg($"  stats.json: {statDict.Count} stats");
                }
            }
            catch (Exception ex)
            {
                LogMsg($"  Achievement fetch failed: {ex.Message}");
            }
        }

        private async Task<List<DlcEntry>> FetchDlcFromStoreAsync(string appId)
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var url = $"https://store.steampowered.com/api/appdetails/?appids={appId}";
                var response = await client.GetStringAsync(url);
                var data = JObject.Parse(response);

                var appData = data[appId]?["data"];
                if (appData == null) return new();

                var name = appData["name"]?.ToString() ?? appId;
                var dlcIds = appData["dlc"] as JArray;
                if (dlcIds == null) return new();

                return dlcIds.Select(id => new DlcEntry
                {
                    AppId = id.ToString(),
                    Name = $"{name} DLC {id}"
                }).ToList();
            }
            catch
            {
                return new();
            }
        }

        /// <summary>
        /// Resolves cloud save directory paths for a platform from cached PICS UFS data.
        /// Ports logic from gbe_fork_tools cloud_dirs.py.
        /// </summary>
        private static List<string> ResolveCloudSaveDirs(string platform, JObject cloudData)
        {
            var paths = new HashSet<string>();

            var saves = cloudData["saves"] as JArray ?? new JArray();
            var overrides = cloudData["overrides"] as JArray ?? new JArray();

            // Filter saves by platform
            var platformSaves = new List<JToken>();
            foreach (var s in saves)
            {
                var platforms = s["platforms"] as JArray;
                if (platforms == null || platforms.Count == 0)
                    platformSaves.Add(s);
                else if (platforms.Any(p => p.ToString().Equals("all", StringComparison.OrdinalIgnoreCase)))
                    platformSaves.Add(s);
                else if (platforms.Any(p => p.ToString().Equals(platform, StringComparison.OrdinalIgnoreCase)))
                    platformSaves.Add(s);
            }

            // Filter overrides by platform
            var platformOverrides = overrides
                .Where(o => (o["platform"]?.ToString() ?? "").Equals(platform, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (platformOverrides.Count > 0)
            {
                foreach (var ov in platformOverrides)
                {
                    var rootNew = ov["root_new"]?.ToString()?.Trim() ?? "";
                    var basePath = $"{{::{rootNew}::}}";
                    var pathAfterRoot = SanitizeCloudPath(ov["path_after_root"]?.ToString() ?? "");
                    if (!string.IsNullOrEmpty(pathAfterRoot))
                        basePath += $"/{pathAfterRoot}";

                    var rootOriginal = (ov["root_original"]?.ToString() ?? "").ToUpper();
                    var matchingSaves = platformSaves.Where(s =>
                        (s["root"]?.ToString() ?? "").ToUpper() == rootOriginal).ToList();

                    foreach (var save in matchingSaves)
                    {
                        var origPath = (save["path"]?.ToString() ?? "").Replace("\\", "/");
                        var transforms = ov["transforms"] as JArray;
                        if (transforms != null)
                        {
                            foreach (var t in transforms)
                            {
                                var find = (t["find"]?.ToString() ?? "").Replace("\\", "/");
                                var replace = (t["replace"]?.ToString() ?? "").Replace("\\", "/");
                                if (!string.IsNullOrEmpty(find) && !string.IsNullOrEmpty(origPath))
                                    origPath = origPath.Replace(find, replace);
                                else if (string.IsNullOrEmpty(find) && string.IsNullOrEmpty(origPath))
                                    origPath = replace;
                            }
                        }

                        origPath = SanitizeCloudPath(origPath);
                        var fullPath = basePath;
                        if (!string.IsNullOrEmpty(origPath))
                            fullPath += $"/{origPath}";

                        paths.Add(FixupVars(fullPath));
                    }
                }
            }
            else
            {
                foreach (var save in platformSaves)
                {
                    var root = (save["root"]?.ToString() ?? "").Trim();
                    var newPath = $"{{::{root}::}}";
                    var pathAfterRoot = SanitizeCloudPath((save["path"]?.ToString() ?? "").Replace("\\", "/"));
                    if (!string.IsNullOrEmpty(pathAfterRoot))
                        newPath += $"/{pathAfterRoot}";
                    paths.Add(FixupVars(newPath));
                }
            }

            return paths.ToList();
        }

        private static string SanitizeCloudPath(string path)
        {
            path = path.Replace("\\", "/").Trim('/');
            while (path.EndsWith("/.")) path = path[..^2];
            while (path.StartsWith("./")) path = path[2..];
            while (path.Contains("/./")) path = path.Replace("/./", "/");
            return path == "." ? "" : path;
        }

        private static string FixupVars(string path)
        {
            return path
                .Replace("{64BitSteamID}", "{::64BitSteamID::}")
                .Replace("{Steam3AccountID}", "{::Steam3AccountID::}");
        }

        private void LogMsg(string msg)
        {
            _logger.Info(msg);
            Log?.Invoke(msg);
        }
    }
}
