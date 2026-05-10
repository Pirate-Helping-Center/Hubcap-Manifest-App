namespace HubcapManifestApp.Helpers
{
    /// <summary>
    /// Well-known Steam directory names and file names used across the application.
    /// All values are relative path segments intended for use with <see cref="System.IO.Path.Combine"/>.
    /// </summary>
    public static class SteamPaths
    {
        // ── Directories ──────────────────────────────────────────
        public const string ConfigDir = "config";
        public const string SteamAppsDir = "steamapps";
        public const string DepotCacheDir = "depotcache";
        public const string StPluginDir = "stplug-in";
        public const string CommonDir = "common";

        // ── Files ────────────────────────────────────────────────
        public const string SteamExe = "steam.exe";
        public const string ConfigVdf = "config.vdf";
        public const string LibraryFoldersVdf = "libraryfolders.vdf";
    }
}
