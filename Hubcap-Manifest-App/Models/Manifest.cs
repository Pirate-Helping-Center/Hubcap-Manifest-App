using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace HubcapManifestApp.Models
{
    public class Manifest : INotifyPropertyChanged
    {
        [JsonProperty("appid")]
        public string AppId { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("description")]
        public string Description { get; set; } = string.Empty;

        [JsonProperty("version")]
        public string Version { get; set; } = "1.0";

        [JsonProperty("size")]
        public long Size { get; set; }

        [JsonProperty("icon_url")]
        public string IconUrl { get; set; } = string.Empty;

        [JsonProperty("last_updated")]
        public DateTime? LastUpdated { get; set; }

        [JsonProperty("download_url")]
        public string DownloadUrl { get; set; } = string.Empty;

        // Non-serialized property for cached icon path
        private string? _cachedIconPath;
        public string? CachedIconPath
        {
            get => _cachedIconPath;
            set
            {
                if (_cachedIconPath != value)
                {
                    _cachedIconPath = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
