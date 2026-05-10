using Newtonsoft.Json;
using HubcapManifestApp.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace HubcapManifestApp.Services
{
    public class SteamApp
    {
        [JsonProperty("appid")]
        public int AppId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;
    }

    public class SteamAppList
    {
        [JsonProperty("apps")]
        public List<SteamApp> Apps { get; set; } = new();
    }

    public class SteamApiResponse
    {
        [JsonProperty("applist")]
        public SteamAppList? AppList { get; set; }

        [JsonProperty("response")]
        public SteamStoreServiceResponse? Response { get; set; }
    }

    // New IStoreService/GetAppList response models
    public class SteamStoreServiceResponse
    {
        [JsonProperty("apps")]
        public List<SteamStoreApp> Apps { get; set; } = new();

        [JsonProperty("have_more_results")]
        public bool HaveMoreResults { get; set; }

        [JsonProperty("last_appid")]
        public int LastAppId { get; set; }
    }

    public class SteamStoreApp
    {
        [JsonProperty("appid")]
        public int AppId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("last_modified")]
        public long LastModified { get; set; }

        [JsonProperty("price_change_number")]
        public long PriceChangeNumber { get; set; }
    }

    // Steam Store Search Models
    public class SteamStoreSearchItem
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("tiny_image")]
        public string TinyImage { get; set; } = string.Empty;

        [JsonProperty("capsule_image")]
        public string CapsuleImage { get; set; } = string.Empty;

        [JsonProperty("header_image")]
        public string HeaderImage { get; set; } = string.Empty;

        [JsonProperty("metascore")]
        public string Metascore { get; set; } = string.Empty;

        [JsonProperty("price")]
        public SteamPrice? Price { get; set; }
    }

    public class SteamPrice
    {
        [JsonProperty("currency")]
        public string Currency { get; set; } = string.Empty;

        [JsonProperty("initial")]
        public int Initial { get; set; }

        [JsonProperty("final")]
        public int Final { get; set; }

        [JsonProperty("discount_percent")]
        public int DiscountPercent { get; set; }

        [JsonProperty("initial_formatted")]
        public string InitialFormatted { get; set; } = string.Empty;

        [JsonProperty("final_formatted")]
        public string FinalFormatted { get; set; } = string.Empty;
    }

    public class SteamStoreSearchResponse
    {
        [JsonProperty("items")]
        public List<SteamStoreSearchItem> Items { get; set; } = new();

        [JsonProperty("total")]
        public int Total { get; set; }
    }

    public class SteamApiService
    {
        private readonly HttpClient _httpClient;
        private readonly CacheService _cacheService;
        private SteamApiResponse? _cachedData;
        private readonly TimeSpan _cacheExpiration = TimeSpan.FromDays(7); // Cache for 7 days

        public SteamApiService(CacheService cacheService, IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient("Default");
            _httpClient.Timeout = TimeSpan.FromSeconds(120);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
            _cacheService = cacheService;
        }

        public async Task<SteamApiResponse?> GetAppListAsync(bool forceRefresh = false)
        {
            // Return in-memory cache if available
            if (!forceRefresh && _cachedData != null)
            {
                return _cachedData;
            }

            // Check disk cache
            if (!forceRefresh && _cacheService.IsSteamAppListCacheValid(_cacheExpiration))
            {
                var (cachedJson, _) = _cacheService.GetCachedSteamAppList();
                if (!string.IsNullOrEmpty(cachedJson))
                {
                    var parsed = ParseAppListJson(cachedJson);
                    if (parsed != null && parsed.AppList?.Apps.Count > 0)
                    {
                        _cachedData = parsed;
                        return _cachedData;
                    }
                }
            }

            try
            {
                var url = AppConstants.HubcapAppListUrl;
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();

                var data = ParseAppListJson(json);
                if (data == null || data.AppList == null || data.AppList.Apps.Count == 0)
                {
                    throw new Exception("Fetched Steam app list was empty or unparseable");
                }

                _cachedData = data;
                // Cache the ORIGINAL upstream JSON so the raw-list shape round-trips
                // through ParseAppListJson next load. (Used to re-serialize the wrapped
                // shape, which then couldn't be decoded by this same method. Regression.)
                _cacheService.CacheSteamAppList(json);
                return data;
            }
            catch (Exception ex)
            {
                var (cachedJson, _) = _cacheService.GetCachedSteamAppList();
                if (!string.IsNullOrEmpty(cachedJson))
                {
                    var parsed = ParseAppListJson(cachedJson);
                    if (parsed != null && parsed.AppList?.Apps.Count > 0)
                    {
                        _cachedData = parsed;
                        return _cachedData;
                    }
                }

                throw new Exception($"Failed to fetch Steam app list: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Parses the Steam app list JSON from any of the three shapes the upstream API
        /// and the on-disk cache have produced over time:
        ///   1. A raw JSON array: [{"appid":7,"name":"Steam Client"}, ...]
        ///   2. A wrapped shape: {"applist":{"apps":[...]}}
        ///   3. A flat dictionary: {"7":"Steam Client", ...}
        /// Returns a SteamApiResponse with a populated AppList on success, null on failure.
        /// </summary>
        private static SteamApiResponse? ParseAppListJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            // Shape 2: wrapped SteamApiResponse
            try
            {
                var wrapped = JsonConvert.DeserializeObject<SteamApiResponse>(json);
                if (wrapped?.AppList?.Apps != null && wrapped.AppList.Apps.Count > 0)
                {
                    return wrapped;
                }
            }
            catch { }

            // Shape 1: plain list
            try
            {
                var apps = JsonConvert.DeserializeObject<List<SteamApp>>(json);
                if (apps != null && apps.Count > 0)
                {
                    return new SteamApiResponse
                    {
                        AppList = new SteamAppList { Apps = apps }
                    };
                }
            }
            catch { }

            // Shape 3: dictionary
            try
            {
                var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (dict != null && dict.Count > 0)
                {
                    var apps = dict.Select(kvp => new SteamApp
                    {
                        AppId = int.TryParse(kvp.Key, out var id) ? id : 0,
                        Name = kvp.Value
                    }).Where(a => a.AppId > 0).ToList();

                    return new SteamApiResponse
                    {
                        AppList = new SteamAppList { Apps = apps }
                    };
                }
            }
            catch { }

            return null;
        }

        public string GetGameName(string appId, SteamApiResponse? steamData = null)
        {
            var data = steamData ?? _cachedData;
            if (data?.AppList?.Apps == null || data.AppList.Apps.Count == 0)
                return AppConstants.UnknownGame;

            var app = data.AppList.Apps.FirstOrDefault(a => a.AppId.ToString() == appId);
            return app?.Name ?? AppConstants.UnknownGame;
        }

        public async Task<string> GetGameNameAsync(string appId)
        {
            var data = await GetAppListAsync();
            return GetGameName(appId, data);
        }

        // Steam Store Search - Matching your bot's implementation
        public async Task<SteamStoreSearchResponse?> SearchStoreAsync(string searchTerm, int limit = 25)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
                return null;

            try
            {
                var cleanedTerm = searchTerm.Trim();

                // Build URL exactly like the working browser URL
                var baseUrl = AppConstants.SteamStoreSearchUrl;
                var queryParams = new Dictionary<string, string>
                {
                    { "term", cleanedTerm },
                    { "l", SteamCmdApiService.DefaultLanguage },
                    { "cc", "US" },
                    { "realm", "1" },
                    { "origin", AppConstants.SteamStoreApiBase },
                    { "f", "jsonfull" },
                    { "start", "0" },
                    { "count", Math.Min(limit * 3, 50).ToString() }
                };

                var queryString = string.Join("&", queryParams.Select(kvp =>
                    $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
                var fullUrl = $"{baseUrl}?{queryString}";

                var response = await _httpClient.GetAsync(fullUrl);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Steam API returned {response.StatusCode}: {errorContent}. URL: {fullUrl}");
                }

                var json = await response.Content.ReadAsStringAsync();
                var searchResponse = JsonConvert.DeserializeObject<SteamStoreSearchResponse>(json);

                return searchResponse;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to search Steam store: {ex.Message}", ex);
            }
        }

    }
}
