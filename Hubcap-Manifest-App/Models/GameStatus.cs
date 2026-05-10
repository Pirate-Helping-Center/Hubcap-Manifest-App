using System;
using Newtonsoft.Json;

namespace HubcapManifestApp.Models
{
    public class GameStatus
    {
        [JsonProperty("app_id")]
        public string AppId { get; set; } = string.Empty;

        [JsonProperty("game_name")]
        public string GameName { get; set; } = string.Empty;

        [JsonProperty("status")]
        public string Status { get; set; } = string.Empty;

        [JsonProperty("manifest_file_exists")]
        public bool? ManifestFileExists { get; set; }

        [JsonProperty("auto_update_enabled")]
        public bool? AutoUpdateEnabled { get; set; }

        [JsonProperty("update_in_progress")]
        public bool? UpdateInProgress { get; set; }

        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonProperty("file_size")]
        public long FileSize { get; set; }

        [JsonProperty("file_modified")]
        public DateTime? FileModified { get; set; }

        [JsonProperty("file_age_days")]
        public double? FileAgeDays { get; set; }

        [JsonProperty("needs_update")]
        public bool? NeedsUpdate { get; set; }

        [JsonProperty("update_reason")]
        public string UpdateReason { get; set; } = string.Empty;
    }
}
