using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace HubcapManifestApp.Models
{
    public class LibraryResponse
    {
        [JsonProperty("status")]
        public string Status { get; set; } = string.Empty;

        [JsonProperty("total_count")]
        public int TotalCount { get; set; }

        [JsonProperty("limit")]
        public int Limit { get; set; }

        [JsonProperty("offset")]
        public int Offset { get; set; }

        [JsonProperty("search")]
        public string? Search { get; set; }

        [JsonProperty("sort_by")]
        public string SortBy { get; set; } = "updated";

        [JsonProperty("games")]
        public List<LibraryGame> Games { get; set; } = new();

        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    public class LibraryGame : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        [JsonProperty("game_id")]
        public string GameId { get; set; } = string.Empty;

        [JsonProperty("game_name")]
        public string GameName { get; set; } = string.Empty;

        [JsonProperty("header_image")]
        public string HeaderImage { get; set; } = string.Empty;

        [JsonProperty("uploaded_date")]
        public DateTime UploadedDate { get; set; }

        [JsonProperty("manifest_available")]
        public bool ManifestAvailable { get; set; }

        [JsonProperty("manifest_size")]
        public long? ManifestSize { get; set; }

        [JsonProperty("manifest_updated")]
        public DateTime? ManifestUpdated { get; set; }

        // For UI
        private string? _cachedIconPath;
        public string? CachedIconPath
        {
            get => _cachedIconPath;
            set => SetProperty(ref _cachedIconPath, value);
        }

        private bool _isInstalled;
        public bool IsInstalled
        {
            get => _isInstalled;
            set => SetProperty(ref _isInstalled, value);
        }

        private bool _hasUpdate;
        public bool HasUpdate
        {
            get => _hasUpdate;
            set => SetProperty(ref _hasUpdate, value);
        }

        private bool _isSelected;
        [Newtonsoft.Json.JsonIgnore]
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }

    public class SearchResponse
    {
        [JsonProperty("status")]
        public string Status { get; set; } = string.Empty;

        [JsonProperty("query")]
        public string Query { get; set; } = string.Empty;

        [JsonProperty("total_matches")]
        public int TotalMatches { get; set; }

        [JsonProperty("returned_count")]
        public int ReturnedCount { get; set; }

        [JsonProperty("limit")]
        public int Limit { get; set; }

        [JsonProperty("results")]
        public List<LibraryGame> Results { get; set; } = new();

        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }
    }
}
