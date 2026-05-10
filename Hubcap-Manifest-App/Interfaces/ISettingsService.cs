using System;
using HubcapManifestApp.Models;

namespace HubcapManifestApp.Interfaces
{
    public interface ISettingsService
    {
        /// <summary>Raised after settings are persisted via SaveSettings.</summary>
        event EventHandler<AppSettings>? SettingsChanged;

        AppSettings LoadSettings();
        void SaveSettings(AppSettings settings);
        void AddApiKeyToHistory(string apiKey);
    }
}
