using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using HubcapManifestApp.Helpers;

namespace HubcapManifestApp.Models
{
    public enum LibraryItemType
    {
        Lua,
        SteamGame
    }

    public class LibraryItem : INotifyPropertyChanged
    {
        public string AppId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public DateTime? InstallDate { get; set; }
        public DateTime? LastUpdated { get; set; }
        public string IconUrl { get; set; } = string.Empty;

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

        private System.Windows.Media.Imaging.BitmapImage? _cachedBitmapImage;
        public System.Windows.Media.Imaging.BitmapImage? CachedBitmapImage
        {
            get => _cachedBitmapImage;
            set
            {
                if (_cachedBitmapImage != value)
                {
                    _cachedBitmapImage = value;
                    OnPropertyChanged();
                }
            }
        }

        public string LocalPath { get; set; } = string.Empty;
        public LibraryItemType ItemType { get; set; }
        public string Version { get; set; } = string.Empty;
        private bool _isSelected;
        [Newtonsoft.Json.JsonIgnore]
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        // Runtime-only: flipped by LibraryViewModel when it loads, true if this Lua's
        // corresponding Steam game is actually present on disk. Not persisted to DB.
        private bool _isInstalledOnSteam;
        [Newtonsoft.Json.JsonIgnore]
        public bool IsInstalledOnSteam
        {
            get => _isInstalledOnSteam;
            set
            {
                if (_isInstalledOnSteam != value)
                {
                    _isInstalledOnSteam = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsLuaUninstalled));
                }
            }
        }

        // Runtime-only: true if this Lua file exists on disk but doesn't match the
        // tool's expected header format ("-- {AppId}'s Lua and Manifest Created by Hubcap",
        // or the legacy "Created by Morrenus" signature from pre-rebrand builds).
        // Drives the Refetch button so the user can overwrite a bad / hand-edited lua
        // with a fresh copy from the API. Not persisted to DB.
        private bool _isForeignFormat;
        [Newtonsoft.Json.JsonIgnore]
        public bool IsForeignFormat
        {
            get => _isForeignFormat;
            set
            {
                if (_isForeignFormat != value)
                {
                    _isForeignFormat = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanRefetchLua));
                }
            }
        }

        /// <summary>
        /// A refetch is only meaningful for numeric AppIDs (the API is keyed by AppID).
        /// Shows the Refetch button only when the lua is flagged as foreign-format AND
        /// the filename parses as a positive integer.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool CanRefetchLua =>
            IsForeignFormat && uint.TryParse(AppId, out var id) && id > 0;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public string SizeFormatted => FormatHelper.FormatBytes(SizeBytes);
        public string TypeBadge => ItemType switch
        {
            LibraryItemType.Lua => "LUA",
            _ => "GAME"
        };

        /// <summary>
        /// A lua item is "not installed" when we have the lua file but no matching Steam
        /// install on disk. Drives the Install button on Library cards. Uses the
        /// runtime-only IsInstalledOnSteam flag because cached SizeBytes from older
        /// scans can be misleading (it may be the .lua file size, not the game size).
        /// </summary>
        public bool IsLuaUninstalled => ItemType == LibraryItemType.Lua && !IsInstalledOnSteam;

        public static LibraryItem FromGame(Game game)
        {
            return new LibraryItem
            {
                AppId = game.AppId,
                Name = game.Name,
                Description = game.Description,
                SizeBytes = game.SizeBytes,
                InstallDate = game.InstallDate,
                LastUpdated = game.LastUpdated,
                IconUrl = game.IconUrl,
                LocalPath = game.LocalPath,
                ItemType = LibraryItemType.Lua,
                Version = game.Version
            };
        }

        public static LibraryItem FromSteamGame(SteamGame steamGame)
        {
            return new LibraryItem
            {
                AppId = steamGame.AppId,
                Name = steamGame.Name,
                SizeBytes = steamGame.SizeOnDisk,
                LastUpdated = steamGame.LastUpdated,
                LocalPath = steamGame.LibraryPath,
                ItemType = LibraryItemType.SteamGame
            };
        }
    }
}
