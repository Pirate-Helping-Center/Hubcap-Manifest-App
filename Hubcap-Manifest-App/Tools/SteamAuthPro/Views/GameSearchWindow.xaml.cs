using System;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Newtonsoft.Json.Linq;
using HubcapManifestApp.Helpers;

namespace HubcapManifestApp.Tools.SteamAuthPro.Views
{
    public partial class GameSearchWindow : Window
    {
        public string? SelectedAppId { get; private set; }

        public GameSearchWindow()
        {
            InitializeComponent();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SearchButton_Click(sender, e);
            }
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            var searchTerm = SearchTextBox.Text.Trim();
            if (string.IsNullOrEmpty(searchTerm))
            {
                MessageBoxHelper.Show("Please enter a game name to search.", "Empty Search", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResultsListBox.Items.Clear();
            ResultsListBox.Items.Add("Searching...");

            try
            {
                using var client = new HttpClient();
                var url = $"{AppConstants.SteamStoreSearchUrl}?term={Uri.EscapeDataString(searchTerm)}&cc=US";
                var response = await client.GetStringAsync(url);
                var json = JObject.Parse(response);

                var items = json["items"] as JArray;
                ResultsListBox.Items.Clear();

                if (items == null || items.Count == 0)
                {
                    ResultsListBox.Items.Add("No results found. Try a different search term.");
                    return;
                }

                foreach (var item in items.Take(20))
                {
                    var appName = item["name"]?.Value<string>() ?? "Unknown";
                    var appId = item["id"]?.Value<string>() ?? "N/A";

                    var listItem = new ListBoxItem
                    {
                        Content = $"{appName} (App ID: {appId})",
                        Tag = appId
                    };
                    ResultsListBox.Items.Add(listItem);
                }
            }
            catch (Exception ex)
            {
                ResultsListBox.Items.Clear();
                ResultsListBox.Items.Add($"Error: {ex.Message}");
                MessageBoxHelper.Show($"Failed to search Steam store: {ex.Message}", "Search Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResultsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            SelectGame();
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            SelectGame();
        }

        private void SelectGame()
        {
            if (ResultsListBox.SelectedItem is ListBoxItem item && item.Tag is string appId)
            {
                SelectedAppId = appId;
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBoxHelper.Show("Please select a game from the list.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
