using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;

namespace HubcapManifestApp.ViewModels
{
    public partial class ToolsViewModel : ObservableObject
    {
        [ObservableProperty]
        private int _selectedTabIndex = 0;

        [RelayCommand]
        private void OpenSteamTools()
        {
            OpenUrl("https://www.steamtools.net/download.html");
        }

        [RelayCommand]
        private void OpenManifestSite()
        {
            OpenUrl("https://hubcapmanifest.com/");
        }

        [RelayCommand]
        private void OpenDiscord()
        {
            OpenUrl("https://discord.gg/hubcapsmanifest");
        }

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Handle error silently
            }
        }
    }
}
