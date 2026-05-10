using Microsoft.Extensions.DependencyInjection;
using HubcapManifestApp.Services;
using HubcapManifestApp.ViewModels;
using HubcapManifestApp.Views.Dialogs;
using System.Windows;
using System.Windows.Controls;

namespace HubcapManifestApp.Views
{
    public partial class GBEDenuvoControl : UserControl
    {
        private static bool _hasShownApiKeyWarning = false;

        public GBEDenuvoControl()
        {
            InitializeComponent();

            // Resolve ViewModel via DI
            if (Application.Current is App app)
            {
                DataContext = app.Services.GetRequiredService<GBEDenuvoViewModel>();
            }

            // Show API key info when control becomes visible for the first time
            if (!_hasShownApiKeyWarning)
            {
                IsVisibleChanged += GBEDenuvoControl_IsVisibleChanged;
            }
        }

        private void GBEDenuvoControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Only show when becoming visible, not when hiding
            if (IsVisible && !_hasShownApiKeyWarning)
            {
                _hasShownApiKeyWarning = true;
                IsVisibleChanged -= GBEDenuvoControl_IsVisibleChanged;

                // Defer the MessageBox to avoid dispatcher issues
                Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    SettingsService settingsService;
                    if (Application.Current is App app)
                    {
                        settingsService = app.Services.GetRequiredService<SettingsService>();
                    }
                    else
                    {
                        return; // DI not available, skip API key check
                    }
                    var settings = settingsService.LoadSettings();

                    if (string.IsNullOrWhiteSpace(settings.GBESteamWebApiKey))
                    {
                        CustomMessageBox.Show(
                            "The GBE Token Generator requires a Steam Web API key to function.\n\n" +
                            "Please set your API key in:\n" +
                            "Settings → Advanced Tools → GBE Token Generator\n\n" +
                            "You can get a free API key at:\n" +
                            "https://steamcommunity.com/dev/apikey",
                            "Steam Web API Key Required",
                            CustomMessageBoxButton.OK);
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }
    }
}
