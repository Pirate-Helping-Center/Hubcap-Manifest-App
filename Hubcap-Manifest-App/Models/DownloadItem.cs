using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using HubcapManifestApp.Helpers;

namespace HubcapManifestApp.Models
{
    public enum DownloadStatus
    {
        Queued,
        Downloading,
        Completed,
        Failed,
        Cancelled
    }

    public class DownloadItem : INotifyPropertyChanged
    {
        private double _progress;
        private DownloadStatus _status;
        private string _statusMessage = string.Empty;
        private long _downloadedBytes;
        private long _totalBytes;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string AppId { get; set; } = string.Empty;
        public string GameName { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string DestinationPath { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public bool IsDepotDownloaderMode { get; set; } = false; // If true, skip auto-install (files are downloaded directly, not as zip)

        // Metadata used by the install marker writer (.hubcapmanifestapp/install.json, or legacy
        // .solusmanifestapp/ on pre-rebrand installs) on success.
        // Set by the caller when a DepotDownloader install kicks off.
        public ulong InstallManifestId { get; set; } = 0;
        public List<uint> InstallDepotIds { get; set; } = new();

        public double Progress
        {
            get => _progress;
            set
            {
                _progress = value;
                OnPropertyChanged();
            }
        }

        public DownloadStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                _statusMessage = value;
                OnPropertyChanged();
            }
        }

        public long DownloadedBytes
        {
            get => _downloadedBytes;
            set
            {
                _downloadedBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DownloadedFormatted));
            }
        }

        public long TotalBytes
        {
            get => _totalBytes;
            set
            {
                _totalBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalFormatted));
            }
        }

        public string DownloadedFormatted => FormatHelper.FormatBytes(DownloadedBytes);
        public string TotalFormatted => FormatHelper.FormatBytes(TotalBytes);

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
