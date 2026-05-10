using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HubcapManifestApp.Services
{
    public class LuaDepotInfo
    {
        public string DepotId { get; set; } = "";
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public bool IsTokenBased { get; set; }
        public string? DlcAppId { get; set; }
        public string? DlcName { get; set; }
    }

    public class LuaParser
    {
        /// <summary>
        /// Extracts all AppIDs that have addtoken() calls
        /// </summary>
        public HashSet<string> ParseTokenAppIds(string luaContent)
        {
            var tokenAppIds = new HashSet<string>();
            var lines = luaContent.Split('\n');

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                // Match: addtoken(3282720, "186020997252537705")
                var tokenMatch = Regex.Match(trimmedLine, @"addtoken\((\d+)");
                if (tokenMatch.Success)
                {
                    var appId = tokenMatch.Groups[1].Value;
                    tokenAppIds.Add(appId);
                }
            }

            return tokenAppIds;
        }

        public List<(string AppId, string Token)> ParseTokens(string luaContent)
        {
            var tokens = new List<(string AppId, string Token)>();
            var lines = luaContent.Split('\n');

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                if (trimmedLine.StartsWith("--"))
                    continue;

                if (trimmedLine.Contains("--"))
                    trimmedLine = trimmedLine.Split("--", 2)[0].Trim();

                var tokenMatch = Regex.Match(trimmedLine, @"addtoken\s*\(\s*(\d+)\s*,\s*[""'](\d+)[""']\s*\)", RegexOptions.IgnoreCase);
                if (tokenMatch.Success)
                {
                    var appId = tokenMatch.Groups[1].Value;
                    var token = tokenMatch.Groups[2].Value;
                    tokens.Add((appId, token));
                }
            }

            return tokens;
        }

        public List<LuaDepotInfo> ParseDepotsFromLua(string luaContent, string? mainAppId = null)
        {
            var depots = new List<LuaDepotInfo>();
            var lines = luaContent.Split('\n');
            var depotMap = new Dictionary<string, LuaDepotInfo>();

            // Get token-based AppIDs to filter them out
            var tokenAppIds = ParseTokenAppIds(luaContent);

            // Track current DLC context by looking for DLC section comments
            string? currentDlcId = null;
            string? currentDlcName = null;

            // First pass: Parse addappid lines to get depot IDs and names
            for (int i = 0; i < lines.Length; i++)
            {
                var trimmedLine = lines[i].Trim();

                // Check for DLC section comments like "-- SILENT HILL f - Bonus Content (AppID: 3282720)"
                var dlcCommentMatch = Regex.Match(trimmedLine, @"--\s*(.+?)\s*\(AppID:\s*(\d+)\)");
                if (dlcCommentMatch.Success)
                {
                    currentDlcName = dlcCommentMatch.Groups[1].Value.Trim();
                    var dlcId = dlcCommentMatch.Groups[2].Value;
                    currentDlcId = dlcId;
                    continue;
                }

                // Reset DLC context when we see a main section (base game)
                if (trimmedLine.StartsWith("-- Base") || trimmedLine.Contains("(Base Game)"))
                {
                    currentDlcId = null;
                    currentDlcName = null;
                    continue;
                }

                // Match: addappid(285311, 1, "hash") -- Rollercoaster Tycoon Content
                var addAppIdMatch = Regex.Match(trimmedLine, @"addappid\((\d+)(?:,.*?)?\)\s*--\s*(.+)");
                if (addAppIdMatch.Success)
                {
                    var depotId = addAppIdMatch.Groups[1].Value;
                    var depotName = addAppIdMatch.Groups[2].Value.Trim();

                    // Check if this depot is token-based
                    bool isTokenBased = tokenAppIds.Contains(depotId) ||
                                       (currentDlcId != null && tokenAppIds.Contains(currentDlcId));

                    if (!depotMap.ContainsKey(depotId))
                    {
                        depotMap[depotId] = new LuaDepotInfo
                        {
                            DepotId = depotId,
                            Name = depotName,
                            Size = 0,
                            IsTokenBased = isTokenBased,
                            DlcAppId = currentDlcId,
                            DlcName = currentDlcName
                        };
                    }
                }
            }

            // Second pass: Parse setManifestid lines to get sizes
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                // Match: setManifestid(285311, "2914580416607481530", 856171654)
                var setManifestMatch = Regex.Match(trimmedLine, @"setManifestid\((\d+),\s*""[^""]*"",\s*(\d+)\)");
                if (setManifestMatch.Success)
                {
                    var depotId = setManifestMatch.Groups[1].Value;
                    var size = long.Parse(setManifestMatch.Groups[2].Value);

                    // Check if this depot is token-based
                    bool isTokenBased = tokenAppIds.Contains(depotId);

                    if (depotMap.ContainsKey(depotId))
                    {
                        depotMap[depotId].Size = size;
                    }
                    else
                    {
                        // Depot has manifest but wasn't in addappid (might be shared depot)
                        depotMap[depotId] = new LuaDepotInfo
                        {
                            DepotId = depotId,
                            Name = $"Depot {depotId}",
                            Size = size,
                            IsTokenBased = isTokenBased
                        };
                    }
                }
            }

            // Add depots to list (prefer ones with manifests, but also include DLCs without manifests)
            // Filter out the main AppID if provided
            foreach (var kvp in depotMap)
            {
                // Don't include the main AppID in the depot list
                if (mainAppId == null || kvp.Key != mainAppId)
                {
                    depots.Add(kvp.Value);
                }
            }

            return depots;
        }

        public Dictionary<string, ulong> ParseManifestIds(string luaContent)
        {
            var manifestIds = new Dictionary<string, ulong>();
            var lines = luaContent.Split('\n');

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                if (trimmedLine.StartsWith("--"))
                    continue;

                var match = Regex.Match(trimmedLine, @"setManifestid\s*\(\s*(\d+)\s*,\s*""(\d+)""");
                if (match.Success)
                {
                    var depotId = match.Groups[1].Value;
                    if (ulong.TryParse(match.Groups[2].Value, out var manifestId))
                    {
                        manifestIds[depotId] = manifestId;
                    }
                }
            }

            return manifestIds;
        }

        public ulong GetPrimaryManifestId(string luaContent, string appId)
        {
            var manifestIds = ParseManifestIds(luaContent);

            if (manifestIds.Count == 0)
                return 0;

            var mainDepotId = (uint.Parse(appId) + 1).ToString();
            if (manifestIds.TryGetValue(mainDepotId, out var mainManifestId))
                return mainManifestId;

            return manifestIds.Values.Max();
        }
    }
}
