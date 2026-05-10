using System;
using HubcapManifestApp.Helpers;

namespace HubcapManifestApp.Models
{
    public class Game
    {
        public string AppId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string Version { get; set; } = "1.0";
        public DateTime? InstallDate { get; set; }
        public DateTime? LastUpdated { get; set; }
        public string IconUrl { get; set; } = string.Empty;
        public bool IsInstalled { get; set; }
        public string LocalPath { get; set; } = string.Empty;

        public string SizeFormatted => FormatHelper.FormatBytes(SizeBytes);
    }
}
