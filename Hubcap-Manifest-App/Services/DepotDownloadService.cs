using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace HubcapManifestApp.Services
{
    public class DepotInfo
    {
        public string DepotId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Language { get; set; }
        public long Size { get; set; }
        public bool IsLanguageSpecific { get; set; }
        public string? DecryptionKey { get; set; }
        public bool IsSelected { get; set; } = true;
        public bool IsTokenBased { get; set; }
        public string? DlcAppId { get; set; }
        public string? DlcName { get; set; }
        public bool IsMainAppId { get; set; }
    }

    public class LanguageOption
    {
        public string Language { get; set; } = "";
        public List<string> RequiredDepots { get; set; } = new();
        public long TotalSize { get; set; }
    }

    public class DepotDownloadService
    {
        private readonly HttpClient _httpClient;
        private readonly LuaParser _luaParser;

        public DepotDownloadService(LuaParser luaParser, IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient("Default");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            _luaParser = luaParser;
        }

        public async Task<List<DepotInfo>> GetDepotsFromSteamCMD(string appId)
        {
            try
            {
                var url = $"{SteamCmdApiService.BaseUrl}/{appId}";
                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    return new List<DepotInfo>();
                }

                var json = await response.Content.ReadAsStringAsync();
                dynamic? data = JsonConvert.DeserializeObject<dynamic>(json);

                var depots = new List<DepotInfo>();

                if (data?["data"]?[appId]?["depots"] != null)
                {
                    var depotsSection = data["data"][appId]["depots"];

                    foreach (var depot in depotsSection)
                    {
                        var depotId = depot.Name;

                        // Skip non-numeric depot IDs (like "branches")
                        if (!long.TryParse(depotId, out long _))
                            continue;

                        var depotData = depot.Value;
                        var config = depotData["config"];

                        long size = 0;
                        if (config?["depotfromapp"] == null && depotData["manifests"]?["public"]?["size"] != null)
                        {
                            size = (long)(depotData["manifests"]["public"]["size"] ?? 0);
                        }

                        var depotInfo = new DepotInfo
                        {
                            DepotId = depotId,
                            Language = config?["language"]?.ToString(),
                            Size = size
                        };

                        // If no language specified, assume English
                        if (string.IsNullOrEmpty(depotInfo.Language))
                        {
                            depotInfo.Language = SteamCmdApiService.DefaultLanguage;
                            depotInfo.IsLanguageSpecific = false;
                        }
                        else
                        {
                            depotInfo.IsLanguageSpecific = true;
                        }

                        depots.Add(depotInfo);
                    }
                }

                return depots;
            }
            catch
            {
                return new List<DepotInfo>();
            }
        }

        public List<LanguageOption> AnalyzeLanguageOptions(List<DepotInfo> depots)
        {
            var languageOptions = new List<LanguageOption>();

            // Get the base depot (usually the one without specific language or the largest)
            var baseDepot = depots.FirstOrDefault(d => !d.IsLanguageSpecific)
                ?? depots.OrderByDescending(d => d.Size).FirstOrDefault();

            if (baseDepot == null)
                return languageOptions;

            // Group by language
            var languageGroups = depots
                .Where(d => !string.IsNullOrEmpty(d.Language))
                .GroupBy(d => d.Language);

            foreach (var group in languageGroups)
            {
                var language = group.Key ?? SteamCmdApiService.DefaultLanguage;
                var languageDepots = group.ToList();

                // Check if language-specific depots are close in size to base depot
                var totalLanguageSize = languageDepots.Sum(d => d.Size);
                var baseSize = baseDepot.Size;

                var requiredDepots = new List<string>();

                // If language depot is close to base size (within few hundred MB), it's complete
                if (totalLanguageSize > 0 && Math.Abs(totalLanguageSize - baseSize) < 500_000_000) // 500 MB threshold
                {
                    // Language depot has full game, don't need base
                    requiredDepots = languageDepots.Select(d => d.DepotId).ToList();
                }
                else
                {
                    // Need base depot + language depot
                    requiredDepots.Add(baseDepot.DepotId);
                    requiredDepots.AddRange(languageDepots.Select(d => d.DepotId));
                }

                languageOptions.Add(new LanguageOption
                {
                    Language = language,
                    RequiredDepots = requiredDepots,
                    TotalSize = requiredDepots.Sum(id => depots.FirstOrDefault(d => d.DepotId == id)?.Size ?? 0)
                });
            }

            // If no language-specific depots, just use base
            if (!languageOptions.Any() && baseDepot != null)
            {
                languageOptions.Add(new LanguageOption
                {
                    Language = SteamCmdApiService.DefaultLanguage,
                    RequiredDepots = new List<string> { baseDepot.DepotId },
                    TotalSize = baseDepot.Size
                });
            }

            return languageOptions;
        }

        /// <summary>
        /// Combines depot info from lua file (names, sizes) with SteamCMD data (languages)
        /// </summary>
        public async Task<List<DepotInfo>> GetCombinedDepotInfo(string appId, string luaContent)
        {
            // Parse depot info from lua file (filter out main AppID)
            var luaDepots = _luaParser.ParseDepotsFromLua(luaContent, appId);

            // Get language info from SteamCMD
            var steamCmdDepots = await GetDepotsFromSteamCMD(appId);

            // Combine the data
            var combinedDepots = new List<DepotInfo>();

            foreach (var luaDepot in luaDepots)
            {
                // Find matching SteamCMD depot for language info
                var steamDepot = steamCmdDepots.FirstOrDefault(d => d.DepotId == luaDepot.DepotId);

                var depotInfo = new DepotInfo
                {
                    DepotId = luaDepot.DepotId,
                    Name = luaDepot.Name,
                    Size = luaDepot.Size,
                    Language = steamDepot?.Language ?? "Unknown",
                    IsLanguageSpecific = steamDepot?.IsLanguageSpecific ?? false,
                    IsSelected = true,
                    IsTokenBased = luaDepot.IsTokenBased,
                    DlcAppId = luaDepot.DlcAppId,
                    DlcName = luaDepot.DlcName
                };

                combinedDepots.Add(depotInfo);
            }

            return combinedDepots;
        }
    }
}

