using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HubcapManifestApp.Services;
using System.Diagnostics;

namespace HubcapManifestApp.ViewModels
{
    public partial class SupportViewModel : ObservableObject
    {
        private readonly LoggerService _logger;

        public SupportViewModel(LoggerService logger)
        {
            _logger = logger;
        }

        [RelayCommand]
        private void OpenLogsFolder()
        {
            _logger.Info("User opened logs folder from Support tab");
            _logger.OpenLogsFolder();
        }

        [RelayCommand]
        private void OpenDiscord()
        {
            _logger.Info("User opened Discord link from Support tab");
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://discord.gg/hubcapsmanifest",
                UseShellExecute = true
            });
        }

        [RelayCommand]
        private void OpenGitHub()
        {
            _logger.Info("User opened GitHub link from Support tab");
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/Hubcap-manifest/Hubcap-Manifest-App",
                UseShellExecute = true
            });
        }
    }
}
