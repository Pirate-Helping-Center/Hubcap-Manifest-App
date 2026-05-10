namespace HubcapManifestApp.Helpers
{
    /// <summary>
    /// Application-wide sentinel values and shared string constants.
    /// </summary>
    public static class AppConstants
    {
        /// <summary>
        /// Sentinel returned by <see cref="Services.SteamApiService.GetGameName"/> when no match is found.
        /// Consumers should compare against this constant rather than the raw string.
        /// </summary>
        public const string UnknownGame = "Unknown Game";

        /// <summary>
        /// Label used in language selection to indicate no language filter should be applied.
        /// </summary>
        public const string AllLanguagesLabel = "All (Skip Filter)";

        /// <summary>
        /// Base URL for the Steam Store Web API (no trailing slash).
        /// </summary>
        public const string SteamStoreApiBase = "https://store.steampowered.com";

        /// <summary>
        /// Steam Store search endpoint.
        /// </summary>
        public const string SteamStoreSearchUrl = SteamStoreApiBase + "/api/storesearch/";

        /// <summary>
        /// Steam Store app details endpoint (no trailing slash — append query string directly).
        /// </summary>
        public const string SteamStoreAppDetailsUrl = SteamStoreApiBase + "/api/appdetails";

        /// <summary>
        /// Folder name used under <c>%APPDATA%</c> (and other special-folder roots) for all
        /// application data — settings, logs, caches, databases, etc.
        /// </summary>
        public const string AppDataFolderName = "HubcapManifestApp";

        /// <summary>
        /// Cloudflare-backed Steam CDN base for app header images.
        /// Append <c>/{appId}/header.jpg</c>.
        /// </summary>
        public const string SteamCdnCloudflare = "https://cdn.cloudflare.steamstatic.com/steam/apps";

        /// <summary>
        /// Akamai-backed Steam CDN base for app header images.
        /// Append <c>/{appId}/header.jpg</c>.
        /// </summary>
        public const string SteamCdnAkamai = "https://cdn.akamai.steamstatic.com/steam/apps";

        /// <summary>
        /// Hubcap app-list API URL (returns Steam app ID / name mappings).
        /// Hosted on the legacy <c>applist.morrenus.xyz</c> domain; not slated for migration.
        /// </summary>
        public const string HubcapAppListUrl = "https://applist.morrenus.xyz/";
    }
}
