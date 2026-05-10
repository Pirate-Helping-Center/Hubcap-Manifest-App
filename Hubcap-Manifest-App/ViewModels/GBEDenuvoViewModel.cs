using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HubcapManifestApp.Helpers;
using HubcapManifestApp.Services;
using HubcapManifestApp.Services.GBE;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace HubcapManifestApp.ViewModels
{
    public partial class GBEDenuvoViewModel : ObservableObject
    {
        private readonly SettingsService _settingsService;

        [ObservableProperty]
        private string _appId = string.Empty;

        [ObservableProperty]
        private string _outputPath = string.Empty;

        public GBEDenuvoViewModel(SettingsService settingsService)
        {
            _settingsService = settingsService;
            var settings = _settingsService.LoadSettings();

            // Load saved path or use Desktop as default
            OutputPath = !string.IsNullOrEmpty(settings.GBETokenOutputPath)
                ? settings.GBETokenOutputPath
                : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        [ObservableProperty]
        private string _logOutput = "Ready to generate tokens. Make sure Steam is running and you own the game.\n";

        [ObservableProperty]
        private bool _isGenerating;

        public bool IsNotGenerating => !IsGenerating;

        partial void OnIsGeneratingChanged(bool value)
        {
            OnPropertyChanged(nameof(IsNotGenerating));
        }

        [RelayCommand]
        private void BrowseOutputPath()
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select output directory for token files",
                SelectedPath = OutputPath
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                OutputPath = dialog.SelectedPath;

                // Save to settings
                var settings = _settingsService.LoadSettings();
                settings.GBETokenOutputPath = OutputPath;
                _settingsService.SaveSettings(settings);
            }
        }

        [RelayCommand]
        private async Task GenerateToken()
        {
            if (!int.TryParse(AppId, out int appIdInt))
            {
                MessageBoxHelper.Show("Please enter a valid numeric App ID.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(OutputPath) || !Directory.Exists(OutputPath))
            {
                MessageBoxHelper.Show("Please select a valid output directory.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Check for API key
            var settings = _settingsService.LoadSettings();
            if (string.IsNullOrWhiteSpace(settings.GBESteamWebApiKey))
            {
                MessageBoxHelper.Show("Please set your Steam Web API key in Settings → GBE Token Generator.\n\nYou can get one at: https://steamcommunity.com/dev/apikey",
                    "API Key Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsGenerating = true;
            LogOutput = string.Empty;

            try
            {
                Log("Starting token generation...");
                Log($"App ID: {appIdInt}");
                Log($"Output: {OutputPath}\n");

                string finalZipPath = Path.Combine(OutputPath, $"Token [{appIdInt}].zip");
                var generator = new GoldbergLogic(appIdInt, finalZipPath, settings.GBESteamWebApiKey, (message, isError) =>
                {
                    Application.Current.Dispatcher.Invoke(() => Log(message, isError));
                });

                bool success = await generator.GenerateAsync();

                if (success)
                {
                    Log($"\n✓ Archive created successfully at: {finalZipPath}");
                    MessageBoxHelper.Show($"Token generated successfully!\n\nSaved to: {finalZipPath}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBoxHelper.Show("The operation failed. Please check the log for details.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Log($"\nError: {ex.Message}", isError: true);
                MessageBoxHelper.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsGenerating = false;
            }
        }

        private void Log(string message, bool isError = false)
        {
            var sb = new StringBuilder(LogOutput);
            sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            LogOutput = sb.ToString();
        }
    }
}
