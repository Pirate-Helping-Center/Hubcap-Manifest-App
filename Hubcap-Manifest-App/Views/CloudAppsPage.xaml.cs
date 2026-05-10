using System.Windows.Controls;
using HubcapManifestApp.ViewModels;

namespace HubcapManifestApp.Views
{
    public partial class CloudAppsPage : UserControl
    {
        public CloudAppsPage() => InitializeComponent();

        private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is CloudAppsViewModel vm && vm.Apps.Count == 0)
                await vm.RefreshCommand.ExecuteAsync(null);
        }
    }
}
