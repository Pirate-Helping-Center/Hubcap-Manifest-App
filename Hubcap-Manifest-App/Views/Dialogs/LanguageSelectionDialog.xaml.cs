using HubcapManifestApp.Helpers;
using HubcapManifestApp.Services;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace HubcapManifestApp.Views.Dialogs
{
    public partial class LanguageSelectionDialog : Window
    {
        public string SelectedLanguage { get; private set; } = SteamCmdApiService.DefaultLanguage;

        public LanguageSelectionDialog(List<string> availableLanguages)
        {
            InitializeComponent();

            var languagesWithAll = new List<string> { AppConstants.AllLanguagesLabel };
            languagesWithAll.AddRange(availableLanguages);
            LanguageListBox.ItemsSource = languagesWithAll;

            if (availableLanguages.Contains("English"))
            {
                LanguageListBox.SelectedItem = "English";
            }
            else if (availableLanguages.Any())
            {
                LanguageListBox.SelectedIndex = 1;
            }
            else
            {
                LanguageListBox.SelectedIndex = 0;
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
            {
                DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            if (LanguageListBox.SelectedItem != null)
            {
                SelectedLanguage = LanguageListBox.SelectedItem.ToString() ?? SteamCmdApiService.DefaultLanguage;
                DialogResult = true;
                Close();
            }
            else
            {
                Helpers.MessageBoxHelper.Show("Please select a language.", "No Selection",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
