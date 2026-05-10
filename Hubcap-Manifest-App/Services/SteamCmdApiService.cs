using Newtonsoft.Json;
using System.Collections.Generic;

namespace HubcapManifestApp.Services
{
    public class SteamCmdDepotData
    {
        [JsonProperty("data")]
        public Dictionary<string, AppData> Data { get; set; } = new();

        [JsonProperty("status")]
        public string Status { get; set; } = "";
    }

    public class AppData
    {
        [JsonProperty("depots")]
        public Dictionary<string, DepotData> Depots { get; set; } = new();

        [JsonProperty("common")]
        public CommonData Common { get; set; } = new();
    }

    public class CommonData
    {
        [JsonProperty("name")]
        public string Name { get; set; } = "";
    }

    public class DepotData
    {
        [JsonProperty("config")]
        public DepotConfig? Config { get; set; }

        [JsonProperty("manifests")]
        public Dictionary<string, ManifestData>? Manifests { get; set; }

        [JsonProperty("dlcappid")]
        public string? DlcAppId { get; set; }

        [JsonProperty("depotfromapp")]
        public string? DepotFromApp { get; set; }

        [JsonProperty("sharedinstall")]
        public string? SharedInstall { get; set; }
    }

    public class DepotConfig
    {
        [JsonProperty("oslist")]
        public string? OsList { get; set; }

        [JsonProperty("language")]
        public string? Language { get; set; }

        [JsonProperty("lowviolence")]
        public string? LowViolence { get; set; }

        [JsonProperty("realm")]
        public string? Realm { get; set; }
    }

    public class ManifestData
    {
        [JsonProperty("gid")]
        public string? Gid { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }

        [JsonProperty("download")]
        public long Download { get; set; }
    }

    public static class SteamCmdApiService
    {
        /// <summary>
        /// Base URL for the SteamCMD API. Use <c>$"{BaseUrl}/{appId}"</c> for app info requests.
        /// </summary>
        public const string BaseUrl = "https://api.steamcmd.net/v1/info";

        /// <summary>
        /// Default Steam language used when no specific language is selected.
        /// </summary>
        public const string DefaultLanguage = "english";
    }
}
