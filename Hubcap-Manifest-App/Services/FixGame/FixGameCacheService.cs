using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace HubcapManifestApp.Services.FixGame
{
    /// <summary>
    /// Caches app info (name, DLC list) per AppID for Fix Game.
    /// Achievements/stats are NOT cached — always fetched fresh.
    /// </summary>
    public class FixGameCacheService
    {
        private readonly string _cacheDir;
        private readonly string _goldbergDir;

        public FixGameCacheService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _cacheDir = Path.Combine(appData, "HubcapManifestApp", "fix_game_cache");
            _goldbergDir = Path.Combine(_cacheDir, "goldberg");
            Directory.CreateDirectory(_cacheDir);
            Directory.CreateDirectory(_goldbergDir);
        }

        public string GoldbergDir => _goldbergDir;

        #region App Info Cache

        public void SaveAppInfo(CachedAppInfo info)
        {
            var path = Path.Combine(_cacheDir, $"{info.AppId}.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(info, Formatting.Indented));
        }

        public CachedAppInfo? LoadAppInfo(string appId)
        {
            var path = Path.Combine(_cacheDir, $"{appId}.json");
            if (!File.Exists(path)) return null;
            try
            {
                return JsonConvert.DeserializeObject<CachedAppInfo>(File.ReadAllText(path));
            }
            catch { return null; }
        }

        #endregion

        #region Goldberg DLL Cache

        public string? GetGoldbergVersion()
        {
            var path = Path.Combine(_goldbergDir, "version.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        public void SetGoldbergVersion(string version)
        {
            File.WriteAllText(Path.Combine(_goldbergDir, "version.txt"), version);
        }

        public bool HasGoldbergDlls()
        {
            return File.Exists(Path.Combine(_goldbergDir, "steam_api.dll"))
                && File.Exists(Path.Combine(_goldbergDir, "steam_api64.dll"));
        }

        public string GetSteamApiDllPath(bool is64Bit)
        {
            return Path.Combine(_goldbergDir, is64Bit ? "steam_api64.dll" : "steam_api.dll");
        }

        #endregion

        public void SavePicsJson(string appId, string json)
        {
            var path = Path.Combine(_cacheDir, $"{appId}_pics.json");
            File.WriteAllText(path, json);
        }

        public string? LoadPicsJson(string appId)
        {
            var path = Path.Combine(_cacheDir, $"{appId}_pics.json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        #region Lua Parsing

        /// <summary>
        /// Parses a lua file and extracts app info (name, DLC list) for caching.
        /// </summary>
        public static CachedAppInfo ParseLuaForCache(string luaContent, string appId)
        {
            var info = new CachedAppInfo { AppId = appId };
            var lines = luaContent.Split('\n');
            bool inDlcSection = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                // Game name from comment header (line 2)
                if (line.StartsWith("-- ") && string.IsNullOrEmpty(info.GameName) && !line.Contains("Lua and Manifest"))
                {
                    info.GameName = line.Substring(3).Trim();
                    continue;
                }

                // Detect DLC sections
                if (line.Contains("DLC") && line.StartsWith("--"))
                {
                    inDlcSection = true;
                    continue;
                }

                // Main application section resets DLC flag
                if (line.Contains("MAIN APPLICATION") && line.StartsWith("--"))
                {
                    inDlcSection = false;
                    continue;
                }

                if (line.Contains("MAIN APP DEPOTS") && line.StartsWith("--"))
                {
                    inDlcSection = false;
                    continue;
                }

                if (line.Contains("SHARED DEPOTS") && line.StartsWith("--"))
                {
                    inDlcSection = false;
                    continue;
                }

                // Parse addappid lines for DLC
                if (line.StartsWith("addappid("))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"addappid\((\d+)");
                    if (match.Success)
                    {
                        var id = match.Groups[1].Value;

                        // Extract name from comment
                        string dlcName = id;
                        var commentIdx = line.IndexOf("--");
                        if (commentIdx >= 0)
                            dlcName = line.Substring(commentIdx + 2).Trim();

                        // Skip the main app ID
                        if (id == appId) continue;

                        // If in DLC section or line has no depot key (just appid), it's a DLC
                        bool hasDepotKey = line.Contains("\"") && line.Split(',').Length >= 3;
                        if (inDlcSection || !hasDepotKey)
                        {
                            info.DlcList.Add(new DlcEntry { AppId = id, Name = dlcName });
                        }
                    }
                }
            }

            return info;
        }

        #endregion
    }

    public class CachedAppInfo
    {
        public string AppId { get; set; } = "";
        public string GameName { get; set; } = "";
        public List<DlcEntry> DlcList { get; set; } = new();
    }

    public class DlcEntry
    {
        public string AppId { get; set; } = "";
        public string Name { get; set; } = "";
    }
}
