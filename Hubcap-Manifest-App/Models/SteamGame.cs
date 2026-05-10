using System;
using HubcapManifestApp.Helpers;

namespace HubcapManifestApp.Models
{
    public class SteamGame
    {
        public string AppId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string InstallDir { get; set; } = string.Empty;
        public long SizeOnDisk { get; set; }
        public DateTime? LastUpdated { get; set; }
        public string LibraryPath { get; set; } = string.Empty;
        public string StateFlags { get; set; } = string.Empty;
        public bool IsFullyInstalled { get; set; }
        public string BuildId { get; set; } = string.Empty;

        public string SizeFormatted => FormatHelper.FormatBytes(SizeOnDisk);
    }
}
