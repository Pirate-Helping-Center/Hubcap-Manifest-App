using Newtonsoft.Json;
using System;

namespace HubcapManifestApp.Models
{
    public class UserStats
    {
        [JsonProperty("user_id")]
        public string UserId { get; set; } = string.Empty;

        [JsonProperty("username")]
        public string Username { get; set; } = string.Empty;

        [JsonProperty("daily_usage")]
        public int DailyUsage { get; set; }

        [JsonProperty("daily_limit")]
        public int DailyLimit { get; set; }

        [JsonProperty("role_daily_limit")]
        public int RoleDailyLimit { get; set; }

        [JsonProperty("custom_api_limit")]
        public int? CustomApiLimit { get; set; }

        [JsonProperty("using_custom_api_limit")]
        public bool UsingCustomApiLimit { get; set; }

        [JsonProperty("can_make_requests")]
        public bool CanMakeRequests { get; set; }

        [JsonProperty("api_key_expires_at")]
        public DateTime? ApiKeyExpiresAt { get; set; }

        [JsonProperty("api_key_usage_count")]
        public int ApiKeyUsageCount { get; set; }

        public int Remaining => DailyLimit - DailyUsage;
    }
}
