using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Newtonsoft.Json;
using HubcapManifestApp.Models;
using HubcapManifestApp.Services;

namespace HubcapManifestApp.Tools.ConfigVdfKeyExtractor
{
    public partial class ConfigVdfKeyExtractorControl : UserControl
    {
        private readonly SettingsService _settingsService;

        public ConfigVdfKeyExtractorControl()
        {
            InitializeComponent();
            _settingsService = (Application.Current as App)?.Services.GetRequiredService<SettingsService>()
                ?? throw new InvalidOperationException("SettingsService not available from DI");

            // Load saved paths from settings
            LoadSavedPaths();
        }

        public void LoadSavedPaths()
        {
            var settings = _settingsService.LoadSettings();
            TxtFilePath.Text = settings.ConfigVdfPath ?? string.Empty;
            TxtCombinedKeysPath.Text = settings.CombinedKeysPath ?? string.Empty;
        }

        private void BrowseVdf_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "VDF files (*.vdf)|*.vdf|All files (*.*)|*.*",
                Title = "Select config.vdf file"
            };

            string defaultPath = VdfKeyExtractor.GetDefaultSteamConfigPath();
            if (File.Exists(defaultPath))
            {
                openFileDialog.InitialDirectory = Path.GetDirectoryName(defaultPath);
            }

            if (openFileDialog.ShowDialog() == true)
            {
                TxtFilePath.Text = openFileDialog.FileName;
            }
        }

        private void BrowseCombinedKeys_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Key files (*.key)|*.key|All files (*.*)|*.*",
                Title = "Select combinedkeys.key file"
            };

            string currentPath = TxtCombinedKeysPath.Text;
            if (!string.IsNullOrEmpty(currentPath) && File.Exists(currentPath))
            {
                openFileDialog.InitialDirectory = Path.GetDirectoryName(currentPath);
                openFileDialog.FileName = Path.GetFileName(currentPath);
            }

            if (openFileDialog.ShowDialog() == true)
            {
                TxtCombinedKeysPath.Text = openFileDialog.FileName;
            }
        }

        private void Extract_Click(object sender, RoutedEventArgs e)
        {
            string vdfPath = TxtFilePath.Text.Trim();
            string combinedKeysPath = TxtCombinedKeysPath.Text.Trim();

            if (string.IsNullOrEmpty(vdfPath))
            {
                MessageBox.Show("Please specify a config.vdf file path.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!File.Exists(vdfPath))
            {
                MessageBox.Show($"File not found: {vdfPath}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                TxtStatus.Text = "Extracting keys...";
                BtnExtract.IsEnabled = false;

                var result = VdfKeyExtractor.ExtractKeysFromVdf(vdfPath, combinedKeysPath);

                if (result.Success)
                {
                    TxtResults.Text = VdfKeyExtractor.FormatKeysAsText(result.Keys);

                    // Build status message
                    string statusMessage = $"Found {result.TotalExtractedCount} total keys";

                    if (result.SkippedKeysCount > 0)
                    {
                        statusMessage += $" | {result.SkippedKeysCount} already in combinedkeys.key (skipped)";
                    }

                    statusMessage += $" | {result.ValidKeysCount} NEW keys shown";

                    if (result.InvalidKeysCount > 0)
                    {
                        statusMessage += $" | {result.InvalidKeysCount} invalid (ignored)";
                    }

                    TxtStatus.Text = statusMessage;

                    // Show message box with summary
                    string summaryMessage = $"Extraction Complete!\n\n" +
                        $"Total keys found: {result.TotalExtractedCount}\n";

                    if (result.SkippedKeysCount > 0)
                    {
                        summaryMessage += $"Already in combinedkeys.key: {result.SkippedKeysCount}\n";
                    }

                    summaryMessage += $"NEW keys to display: {result.ValidKeysCount}";

                    if (result.InvalidKeysCount > 0)
                    {
                        summaryMessage += $"\nInvalid keys ignored: {result.InvalidKeysCount}";
                    }

                    if (result.ValidKeysCount == 0 && result.SkippedKeysCount > 0)
                    {
                        summaryMessage += "\n\nAll keys already exist in combinedkeys.key - nothing new to add!";
                    }
                    else if (result.ValidKeysCount == 0)
                    {
                        summaryMessage += "\n\nNo depot keys found in the VDF file.";
                    }

                    MessageBox.Show(summaryMessage, "Extraction Results",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    TxtStatus.Text = $"Error: {result.ErrorMessage}";
                    MessageBox.Show(result.ErrorMessage, "Extraction Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "Error during extraction";
                MessageBox.Show($"An error occurred: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnExtract.IsEnabled = true;
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(TxtResults.Text))
            {
                MessageBox.Show("No keys to copy. Please extract keys first.", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                Clipboard.SetText(TxtResults.Text);
                TxtStatus.Text = "Keys copied to clipboard!";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy to clipboard: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(TxtResults.Text))
            {
                MessageBox.Show("No keys to save. Please extract keys first.", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var saveFileDialog = new SaveFileDialog
            {
                Filter = "Key files (*.key)|*.key|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Save Extracted Keys",
                FileName = "extracted_depot_keys.key"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    File.WriteAllText(saveFileDialog.FileName, TxtResults.Text);
                    TxtStatus.Text = $"Keys saved to: {Path.GetFileName(saveFileDialog.FileName)}";
                    MessageBox.Show("Keys saved successfully!", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to save file: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}
