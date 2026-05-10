namespace HubcapManifestApp.Services.CloudRedirect
{
    /// <summary>
    /// Stub for CloudRedirect's localization system. Returns keys as-is (English).
    /// </summary>
    internal static class S
    {
        public static string Get(string key) => key;
        public static string Format(string key, params object[] args)
        {
            try { return string.Format(key, args); }
            catch { return key; }
        }
    }
}
