using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HubcapManifestApp.Helpers;
using HubcapManifestApp.Tools.SteamAuthPro.Models;

namespace HubcapManifestApp.Tools.SteamAuthPro.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly Config _config;

        public SettingsWindow(Config config)
        {
            InitializeComponent();
            _config = config;
            LoadSettings();
        }

        private void LoadSettings()
        {
            ApiUrlTextBox.Text = _config.ApiUrl;
            PhpSessionIdTextBox.Text = _config.PhpSessionId;
            UpdateAccountsList();
        }

        private void UpdateAccountsList()
        {
            AccountsListBox.Items.Clear();

            for (int i = 0; i < _config.Accounts.Count; i++)
            {
                var account = _config.Accounts[i];
                var isActive = i == _config.ActiveAccount ? " [ACTIVE]" : "";
                AccountsListBox.Items.Add($"{account.Name}{isActive}");
            }
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

        private void AddAccountButton_Click(object sender, RoutedEventArgs e)
        {
            var inputDialog = new InputDialog("Add Account", "Account Name:", "")
            {
                Owner = this
            };

            if (inputDialog.ShowDialog() == true)
            {
                var name = inputDialog.Result;
                if (string.IsNullOrWhiteSpace(name))
                    return;

                _config.AddAccount(name.Trim());
                UpdateAccountsList();
            }
        }

        private void RemoveAccountButton_Click(object sender, RoutedEventArgs e)
        {
            if (AccountsListBox.SelectedIndex == -1)
            {
                MessageBoxHelper.Show("Please select an account to remove.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBoxHelper.Show("Are you sure you want to remove this account?", "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _config.RemoveAccount(AccountsListBox.SelectedIndex);
                UpdateAccountsList();
            }
        }

        private void SetActiveButton_Click(object sender, RoutedEventArgs e)
        {
            if (AccountsListBox.SelectedIndex == -1)
            {
                MessageBoxHelper.Show("Please select an account to set as active.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _config.SetActiveAccount(AccountsListBox.SelectedIndex);
            UpdateAccountsList();
        }

        private void AutoDetectButton_Click(object sender, RoutedEventArgs e)
        {
            var steamAccounts = SteamAccountManager.GetSteamAccounts();

            if (steamAccounts.Count == 0)
            {
                MessageBoxHelper.Show("No Steam accounts detected. Make sure Steam is installed and you have logged in accounts.",
                    "No Accounts Found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int addedCount = 0;
            foreach (var kvp in steamAccounts)
            {
                var steamId = kvp.Key;
                var steamAccount = kvp.Value;

                // Check if account already exists by SteamId
                var exists = _config.Accounts.Any(a => a.SteamId == steamId);

                if (!exists)
                {
                    var displayName = string.IsNullOrEmpty(steamAccount.PersonaName)
                        ? $"{steamAccount.AccountName} → {steamId}"
                        : $"{steamAccount.PersonaName} → {steamAccount.AccountName}";

                    _config.AddAccount(displayName, steamId);
                    addedCount++;
                }
            }

            UpdateAccountsList();

            if (addedCount > 0)
            {
                MessageBoxHelper.Show($"Added {addedCount} Steam account(s).",
                    "Auto-Detect Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBoxHelper.Show("All detected Steam accounts are already in the list.",
                    "Auto-Detect Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            _config.ApiUrl = ApiUrlTextBox.Text.Trim();
            _config.PhpSessionId = PhpSessionIdTextBox.Text.Trim();
            _config.Save();
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    // Simple Input Dialog
    public class InputDialog : Window
    {
        public string Result { get; private set; } = string.Empty;
        private readonly TextBox _textBox;

        public InputDialog(string title, string prompt, string defaultValue)
        {
            Title = title;
            Width = 400;
            Height = 180;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None;
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("SteamDarkBrush");
            ResizeMode = ResizeMode.NoResize;

            var border = new Border
            {
                BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("SteamMediumBrush"),
                BorderThickness = new Thickness(1)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Title Bar
            var titleBar = new Grid
            {
                Background = (System.Windows.Media.Brush)Application.Current.FindResource("SteamDarkestBrush")
            };
            titleBar.MouseLeftButtonDown += (s, e) => DragMove();

            var titleText = new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("SteamLightBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            titleBar.Children.Add(titleText);
            Grid.SetRow(titleBar, 0);

            // Prompt
            var promptLabel = new Label
            {
                Content = prompt,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("SteamTextBrush"),
                Margin = new Thickness(20, 15, 20, 5)
            };
            Grid.SetRow(promptLabel, 1);

            // TextBox
            _textBox = new TextBox
            {
                Text = defaultValue,
                Style = (Style)Application.Current.FindResource("SteamTextBoxStyle"),
                Margin = new Thickness(20, 0, 20, 10)
            };
            _textBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                    OkButton_Click(s, e);
            };
            Grid.SetRow(_textBox, 2);

            // Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(20, 10, 20, 20)
            };

            var okButton = new Button
            {
                Content = "OK",
                Width = 100,
                Margin = new Thickness(5),
                Style = (Style)Application.Current.FindResource("SteamButtonStyle")
            };
            okButton.Click += OkButton_Click;

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 100,
                Margin = new Thickness(5),
                Style = (Style)Application.Current.FindResource("SteamButtonStyle")
            };
            cancelButton.Click += (s, e) => { DialogResult = false; Close(); };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            Grid.SetRow(buttonPanel, 3);

            grid.Children.Add(titleBar);
            grid.Children.Add(promptLabel);
            grid.Children.Add(_textBox);
            grid.Children.Add(buttonPanel);

            border.Child = grid;
            Content = border;

            Loaded += (s, e) => _textBox.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Result = _textBox.Text;
            DialogResult = true;
            Close();
        }
    }
}
