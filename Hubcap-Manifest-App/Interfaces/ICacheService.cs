using HubcapManifestApp.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace HubcapManifestApp.Interfaces
{
    public interface ICacheService
    {
        Task<string?> GetIconAsync(string appId, string iconUrl);
        void ClearIconCache();
        void CacheManifests(List<Manifest> manifests);
        List<Manifest>? GetCachedManifests();
        void CacheManifest(Manifest manifest);
        Manifest? GetCachedManifest(string appId);
        bool IsOfflineMode();
        void ClearDataCache();
        void ClearAllCache();
        long GetCacheSize();
        void CacheSteamAppList(string jsonData);
        (string? data, DateTime? timestamp) GetCachedSteamAppList();
        bool IsSteamAppListCacheValid(TimeSpan maxAge);
        void CacheGameStatus(string appId, string jsonData);
        (string? data, DateTime? timestamp) GetCachedGameStatus(string appId);
        bool IsGameStatusCacheValid(string appId, TimeSpan maxAge);
    }
}
