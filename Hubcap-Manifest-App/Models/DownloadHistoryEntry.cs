using System;
using HubcapManifestApp.Helpers;

namespace HubcapManifestApp.Models
{
    public class DownloadHistoryEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string AppId { get; set; } = string.Empty;
        public string GameName { get; set; } = string.Empty;
        public long TotalBytes { get; set; }
        public string Status { get; set; } = string.Empty; // "Completed" or "Failed"
        public string StatusMessage { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string DestinationPath { get; set; } = string.Empty;
        public string Mode { get; set; } = "SteamTools"; // "SteamTools", "DepotDownloader", "Workshop"

        public string SizeFormatted => TotalBytes <= 0 ? "Unknown" : FormatHelper.FormatBytes(TotalBytes);

        public string DurationFormatted
        {
            get
            {
                if (EndTime == null) return "—";
                var duration = EndTime.Value - StartTime;
                if (duration.TotalSeconds < 60) return $"{duration.TotalSeconds:F0}s";
                if (duration.TotalMinutes < 60) return $"{duration.Minutes}m {duration.Seconds}s";
                return $"{duration.Hours}h {duration.Minutes}m";
            }
        }

        public string DateFormatted => StartTime.ToString("MMM d, yyyy h:mm tt");
    }
}
