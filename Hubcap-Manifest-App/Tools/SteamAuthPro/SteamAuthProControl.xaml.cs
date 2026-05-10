using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using HubcapManifestApp.Helpers;
using HubcapManifestApp.Tools.SteamAuthPro.Models;
using HubcapManifestApp.Tools.SteamAuthPro.Views;

namespace HubcapManifestApp.Tools.SteamAuthPro
{
    public partial class SteamAuthProControl : UserControl
    {
        private Config _config = null!;

        public SteamAuthProControl()
        {
            InitializeComponent();
            LoadConfig();
            InitializeControls();
        }

        private void LoadConfig()
        {
            _config = Config.Load();
            SyncActiveAccountWithRegistry();
            PopulateAccountComboBox();
        }

        private void SyncActiveAccountWithRegistry()
        {
            try
            {
                // Get the currently logged in Steam account from registry
                var currentSteamId = SteamAccountManager.GetCurrentSteamAccount();
                if (string.IsNullOrEmpty(currentSteamId))
                    return;

                // Find the matching account in config by SteamId
                for (int i = 0; i < _config.Accounts.Count; i++)
                {
                    if (_config.Accounts[i].SteamId == currentSteamId)
                    {
                        _config.SetActiveAccount(i);
                        _config.Save();
                        break;
                    }
                }
            }
            catch
            {
                // If we can't detect, just use the saved active account
            }
        }

        private void InitializeControls()
        {
            // Populate usage limit combo box
            UsageLimitComboBox.Items.Add(new ComboBoxItem { Content = "1 use", Tag = -1 });
            UsageLimitComboBox.Items.Add(new ComboBoxItem { Content = "2 uses", Tag = 1 });
            UsageLimitComboBox.Items.Add(new ComboBoxItem { Content = "3 uses", Tag = 2 });
            UsageLimitComboBox.Items.Add(new ComboBoxItem { Content = "4 uses", Tag = 3 });
            UsageLimitComboBox.Items.Add(new ComboBoxItem { Content = "5 uses", Tag = 4 });
            UsageLimitComboBox.Items.Add(new ComboBoxItem { Content = "Unlimited", Tag = 5 });
            UsageLimitComboBox.SelectedIndex = 0;
        }

        private void PopulateAccountComboBox()
        {
            // Temporarily disconnect the event handler to prevent triggering account switch on load
            AccountComboBox.SelectionChanged -= AccountComboBox_SelectionChanged;

            AccountComboBox.Items.Clear();

            if (_config.Accounts.Count == 0)
            {
                AccountComboBox.Items.Add(new ComboBoxItem { Content = "No accounts configured", IsEnabled = false });
                AccountComboBox.SelectedIndex = 0;
                AccountComboBox.IsEnabled = false;
                AccountComboBox.SelectionChanged += AccountComboBox_SelectionChanged;
                return;
            }

            foreach (var account in _config.Accounts)
            {
                AccountComboBox.Items.Add(new ComboBoxItem { Content = account.Name });
            }

            if (_config.ActiveAccount.HasValue && _config.ActiveAccount.Value < AccountComboBox.Items.Count)
            {
                AccountComboBox.SelectedIndex = _config.ActiveAccount.Value;
            }

            AccountComboBox.IsEnabled = true;

            // Reconnect the event handler
            AccountComboBox.SelectionChanged += AccountComboBox_SelectionChanged;
        }


        #region Account Management

        private async void AccountComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (AccountComboBox.SelectedIndex >= 0 && AccountComboBox.SelectedIndex < _config.Accounts.Count)
            {
                var selectedAccount = _config.Accounts[AccountComboBox.SelectedIndex];

                // Only switch if there's a Steam ID and it's different from current account
                if (!string.IsNullOrEmpty(selectedAccount.SteamId))
                {
                    var currentSteamId = SteamAccountManager.GetCurrentSteamAccount();

                    if (currentSteamId != selectedAccount.SteamId)
                    {
                        try
                        {
                            UpdateStatus("Switching Steam account...");
                            AccountComboBox.IsEnabled = false;
                            GenerateButton.IsEnabled = false;

                            await Task.Run(() => SteamAccountManager.SwitchSteamAccount(selectedAccount.SteamId));

                            _config.SetActiveAccount(AccountComboBox.SelectedIndex);
                            _config.Save();
                            UpdateStatus($"✓ Switched to {selectedAccount.Name}");
                        }
                        catch (Exception ex)
                        {
                            MessageBoxHelper.Show($"Failed to switch Steam account: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            UpdateStatus("Failed to switch account");

                            // Revert selection
                            AccountComboBox.SelectionChanged -= AccountComboBox_SelectionChanged;
                            if (_config.ActiveAccount.HasValue)
                                AccountComboBox.SelectedIndex = _config.ActiveAccount.Value;
                            AccountComboBox.SelectionChanged += AccountComboBox_SelectionChanged;
                        }
                        finally
                        {
                            AccountComboBox.IsEnabled = true;
                            GenerateButton.IsEnabled = true;
                        }
                    }
                    else
                    {
                        // Already logged in as this account
                        _config.SetActiveAccount(AccountComboBox.SelectedIndex);
                        _config.Save();
                        UpdateStatus($"✓ Switched to {selectedAccount.Name}");
                    }
                }
                else
                {
                    _config.SetActiveAccount(AccountComboBox.SelectedIndex);
                    _config.Save();
                    UpdateStatus($"✓ Switched to {selectedAccount.Name}");
                }
            }
        }

        #endregion

        #region Game Search

        private void SearchGameButton_Click(object sender, RoutedEventArgs e)
        {
            var gameSearchWindow = new GameSearchWindow { Owner = Window.GetWindow(this) };
            if (gameSearchWindow.ShowDialog() == true && !string.IsNullOrEmpty(gameSearchWindow.SelectedAppId))
            {
                AppIdTextBox.Text = gameSearchWindow.SelectedAppId;
                UpdateStatus($"App ID {gameSearchWindow.SelectedAppId} selected");
            }
        }

        #endregion

        #region Code Generation

        private async void GenerateButton_Click(object sender, RoutedEventArgs e)
        {
            var appId = AppIdTextBox.Text.Trim();

            if (string.IsNullOrEmpty(appId) || !appId.All(char.IsDigit))
            {
                MessageBoxHelper.Show("Please enter a valid App ID (numbers only).", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(_config.CurrentPhpSessionId))
            {
                MessageBoxHelper.Show("Please configure an account with PHPSESSID in Settings.", "No Account", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var usageLimitItem = (ComboBoxItem)UsageLimitComboBox.SelectedItem;
            var usageLimit = (int)usageLimitItem.Tag;

            await GenerateAuthCodeAsync(appId, usageLimit);
        }

        private async Task GenerateAuthCodeAsync(string appId, int usageLimit)
        {
            try
            {
                // Disable button
                GenerateButton.IsEnabled = false;
                AuthCodeDisplay.Text = "";
                InfoTextBox.Text = "";
                UpdateStatus("Processing...");

                // Trigger Steam ticket generation
                UpdateStatus("Triggering Steam ticket generation...");
                var steamUrl = _config.TicketMethod == TicketDumpMethod.OpenSteamtools
                    ? $"steam://openstools/dumpticket_steamtools/{appId}"
                    : $"steam://run/tool/geteticket/{appId}";
                Process.Start(new ProcessStartInfo(steamUrl) { UseShellExecute = true });

                UpdateStatus("Waiting for Steam to process...");
                await Task.Delay(2000);

                // Read clipboard
                UpdateStatus("Reading clipboard...");
                var clipboardContent = await ReadClipboardWithRetryAsync();

                if (string.IsNullOrEmpty(clipboardContent))
                {
                    throw new Exception("Failed to get ticket from Steam. Try again.");
                }

                // Parse ticket data
                UpdateStatus("Parsing ticket data...");
                var ticketInfo = ParseTicketData(clipboardContent);

                // Submit to API
                UpdateStatus("Submitting to API...");
                var response = await SubmitToApiAsync(ticketInfo.AppId, ticketInfo.SteamId, ticketInfo.TicketData, usageLimit);

                HandleSuccess(response);
            }
            catch (Exception ex)
            {
                HandleError(ex.Message);
            }
            finally
            {
                GenerateButton.IsEnabled = true;
            }
        }

        private async Task<string> ReadClipboardWithRetryAsync(int maxAttempts = 3, int delayMs = 1500)
        {
            for (int i = 0; i < maxAttempts; i++)
            {
                try
                {
                    var clipboard = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(clipboard) && clipboard.Contains("|"))
                    {
                        return clipboard;
                    }
                }
                catch
                {
                    // Ignore
                }

                if (i < maxAttempts - 1)
                {
                    await Task.Delay(delayMs);
                }
            }

            return string.Empty;
        }

        private (string AppId, string SteamId, string TicketData) ParseTicketData(string clipboardContent)
        {
            var parts = clipboardContent.Split('|');
            if (parts.Length != 3)
            {
                throw new Exception("Invalid clipboard format. Expected: appid|steamid|ticket_data");
            }

            return (parts[0].Trim(), parts[1].Trim(), parts[2].Trim());
        }

        private async Task<JObject> SubmitToApiAsync(string appId, string steamId, string ticketData, int usageLimit)
        {
            using var client = new HttpClient();

            var request = new HttpRequestMessage(HttpMethod.Post, _config.ApiUrl);
            request.Headers.Add("accept", "*/*");
            request.Headers.Add("Cookie", $"PHPSESSID={_config.CurrentPhpSessionId}");
            request.Headers.Add("origin", "https://drm.steam.run");
            request.Headers.Add("referer", "https://drm.steam.run/submit.php");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            var formData = new System.Collections.Generic.Dictionary<string, string>
            {
                { "appid", appId },
                { "steamid", steamId },
                { "ticket_data", ticketData },
                { "usage_limit", usageLimit.ToString() }
            };

            request.Content = new FormUrlEncodedContent(formData);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return JObject.Parse(json);
        }

        private void HandleSuccess(JObject response)
        {
            var success = response["success"]?.Value<bool>() ?? false;
            if (!success)
            {
                var message = response["message"]?.Value<string>() ?? "Unknown error";
                throw new Exception($"API error: {message}");
            }

            var data = response["data"] as JObject;
            var authCode = data?["auth_code"]?.Value<string>() ?? "N/A";
            var expiresIn = data?["expires_in"]?.Value<int>() ?? 1800;
            var usageLimitText = data?["usage_limit_text"]?.Value<string>() ?? "N/A";
            var steamId = data?["steamid"]?.Value<string>() ?? "N/A";
            var appId = data?["appid"]?.Value<string>() ?? "N/A";

            var expirationTime = DateTime.Now.AddSeconds(expiresIn);
            var expirationStr = expirationTime.ToString("yyyy-MM-dd HH:mm:ss");

            AuthCodeDisplay.Text = authCode;
            AuthCodeDisplay.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("SteamLightBrush");

            var info = $"Expires: {expirationStr} ({expiresIn / 60} minutes)\n";
            info += $"Usage Limit: {usageLimitText}\n";
            info += $"AppID: {appId}\n";
            info += $"SteamID: {steamId}";
            InfoTextBox.Text = info;

            try
            {
                var clipboardText = $"Copy {authCode} → Visit https://drm.steam.run/ → Paste code in the field → Click the checkmark button → Authorize Steam → Click 'Run Game'";
                Clipboard.SetText(clipboardText);
                UpdateStatus("✓ Code with instructions copied to clipboard!");
            }
            catch
            {
                UpdateStatus("✓ Code generated!");
            }
        }

        private void HandleError(string errorMessage)
        {
            UpdateStatus($"Error: {errorMessage}");
            AuthCodeDisplay.Text = "ERROR";
            AuthCodeDisplay.Foreground = System.Windows.Media.Brushes.Red;
            InfoTextBox.Text = errorMessage;

            MessageBoxHelper.Show(errorMessage, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        #endregion

        #region Helper Methods

        private void UpdateStatus(string message)
        {
            StatusLabel.Text = message;
        }

        #endregion
    }
}
