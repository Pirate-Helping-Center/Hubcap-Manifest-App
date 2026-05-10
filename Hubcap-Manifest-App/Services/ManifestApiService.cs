using HubcapManifestApp.Interfaces;
using HubcapManifestApp.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace HubcapManifestApp.Services
{
    public class ManifestApiService : IManifestApiService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly CacheService? _cacheService;
        public const string BaseUrl = "https://hubcapmanifest.com/api/v1";
        private readonly TimeSpan _statusCacheExpiration = TimeSpan.FromMinutes(5); // Cache status for 5 minutes

        public ManifestApiService(IHttpClientFactory httpClientFactory, CacheService? cacheService = null)
        {
            _httpClientFactory = httpClientFactory;
            _cacheService = cacheService;
        }

        private HttpClient CreateClient()
        {
            var client = _httpClientFactory.CreateClient("Default");
            client.Timeout = TimeSpan.FromSeconds(30);
            return client;
        }

        private HttpClient CreateAuthenticatedClient(string apiKey)
        {
            var client = CreateClient();
            // Pass API key via Authorization header instead of query string
            // to avoid leaking it in server/proxy logs and browser history
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            return client;
        }

        public async Task<Manifest?> GetManifestAsync(string appId, string apiKey)
        {
            try
            {
                var client = CreateAuthenticatedClient(apiKey);
                var url = $"{BaseUrl}/manifest/{appId}";
                var response = await client.GetAsync(url);
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var preview = json.Length > 200 ? json.Substring(0, 200) : json;
                    throw new Exception($"Manifest not available for App ID {appId}. API returned {response.StatusCode}: {preview}");
                }

                try
                {
                    var manifest = JsonConvert.DeserializeObject<Manifest>(json);
                    return manifest;
                }
                catch (JsonException jex)
                {
                    var preview = json.Length > 200 ? json.Substring(0, 200) : json;
                    throw new Exception($"Invalid JSON from API for App ID {appId}. Response: {preview}", jex);
                }
            }
            catch (Exception ex) when (ex is not JsonException)
            {
                throw new Exception($"Failed to fetch manifest for {appId}: {ex.Message}", ex);
            }
        }

        public async Task<List<Manifest>?> SearchGamesAsync(string query, string apiKey)
        {
            try
            {
                var client = CreateAuthenticatedClient(apiKey);
                var url = $"{BaseUrl}/search?q={Uri.EscapeDataString(query)}";
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"API returned {response.StatusCode}");
                }

                var json = await response.Content.ReadAsStringAsync();
                var results = JsonConvert.DeserializeObject<List<Manifest>>(json);
                return results;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to search games: {ex.Message}", ex);
            }
        }

        public async Task<List<Manifest>?> GetAllGamesAsync(string apiKey)
        {
            try
            {
                var client = CreateAuthenticatedClient(apiKey);
                var url = $"{BaseUrl}/games";
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"API returned {response.StatusCode}");
                }

                var json = await response.Content.ReadAsStringAsync();
                var results = JsonConvert.DeserializeObject<List<Manifest>>(json);
                return results;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to fetch games list: {ex.Message}", ex);
            }
        }

        public bool ValidateApiKey(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.StartsWith("smm", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<bool> TestApiKeyAsync(string apiKey)
        {
            try
            {
                var client = CreateAuthenticatedClient(apiKey);
                var url = $"{BaseUrl}/status/10";
                var response = await client.GetAsync(url);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<GameStatus?> GetGameStatusAsync(string appId, string apiKey)
        {
            // Check cache first if CacheService is available
            if (_cacheService != null && _cacheService.IsGameStatusCacheValid(appId, _statusCacheExpiration))
            {
                var (cachedJson, _) = _cacheService.GetCachedGameStatus(appId);
                if (!string.IsNullOrEmpty(cachedJson))
                {
                    try
                    {
                        return JsonConvert.DeserializeObject<GameStatus>(cachedJson);
                    }
                    catch
                    {
                        // Ignore deserialization errors - will fetch fresh data
                    }
                }
            }

            try
            {
                var client = CreateAuthenticatedClient(apiKey);
                var url = $"{BaseUrl}/status/{appId}";
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var status = JsonConvert.DeserializeObject<GameStatus>(json);

                // Cache the response
                _cacheService?.CacheGameStatus(appId, json);

                return status;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to fetch status for {appId}: {ex.Message}", ex);
            }
        }

        public async Task<LibraryResponse?> GetLibraryAsync(string apiKey, int limit = 100, int offset = 0, string? search = null, string sortBy = "updated")
        {
            try
            {
                var client = CreateAuthenticatedClient(apiKey);
                var url = $"{BaseUrl}/library?limit={limit}&offset={offset}&sort_by={sortBy}";
                if (!string.IsNullOrEmpty(search))
                {
                    url += $"&search={Uri.EscapeDataString(search)}";
                }

                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"API returned {response.StatusCode}");
                }

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<LibraryResponse>(json);
                return result;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to fetch library: {ex.Message}", ex);
            }
        }

        public async Task<SearchResponse?> SearchLibraryAsync(string query, string apiKey, int limit = 50, bool searchByAppId = false)
        {
            try
            {
                var client = CreateAuthenticatedClient(apiKey);
                var url = $"{BaseUrl}/search?q={Uri.EscapeDataString(query)}&limit={limit}&appid={searchByAppId.ToString().ToLower()}";
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"API returned {response.StatusCode}");
                }

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<SearchResponse>(json);
                return result;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to search library: {ex.Message}", ex);
            }
        }
        public async Task<UserStats?> GetUserStatsAsync(string apiKey)
        {
            try
            {
                var client = CreateAuthenticatedClient(apiKey);
                var url = $"{BaseUrl}/user/stats";
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<UserStats>(json);
            }
            catch
            {
                return null;
            }
        }
        /// <summary>
        /// Downloads a workshop manifest file and returns the binary content + metadata from response headers.
        /// </summary>
        public async Task<(byte[] manifestData, WorkshopItem item)?> GetWorkshopManifestAsync(string workshopId, string apiKey)
        {
            try
            {
                var client = CreateAuthenticatedClient(apiKey);
                client.Timeout = TimeSpan.FromSeconds(60);
                var url = $"{BaseUrl}/generate/workshopmanifest/{workshopId}";
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    return null;

                var data = await response.Content.ReadAsByteArrayAsync();
                var item = new WorkshopItem { WorkshopId = workshopId };

                string? GetHeader(string name) =>
                    response.Headers.TryGetValues(name, out var vals) ? System.Linq.Enumerable.FirstOrDefault(vals) : null;

                if (uint.TryParse(GetHeader("X-App-Id"), out var appIdVal))
                    item.AppId = appIdVal;
                if (uint.TryParse(GetHeader("X-Depot-Id"), out var depotIdVal))
                    item.DepotId = depotIdVal;
                if (ulong.TryParse(GetHeader("X-Manifest-Id"), out var manifestIdVal))
                    item.ManifestId = manifestIdVal;
                item.Title = GetHeader("X-Workshop-Title") ?? workshopId;
                item.DepotKey = GetHeader("X-Depot-Key") ?? string.Empty;

                return (data, item);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Fetches raw lua content for an app ID (used to extract depot keys for workshop downloads).
        /// </summary>
        public async Task<string?> GetLuaContentAsync(string appId, string apiKey)
        {
            try
            {
                var client = CreateAuthenticatedClient(apiKey);
                var url = $"{BaseUrl}/lua/{appId}";
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    return null;

                return await response.Content.ReadAsStringAsync();
            }
            catch
            {
                return null;
            }
        }
    }
}
