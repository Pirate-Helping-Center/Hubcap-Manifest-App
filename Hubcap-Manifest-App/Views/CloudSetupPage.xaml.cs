using System.Windows.Controls;
using HubcapManifestApp.ViewModels;

namespace HubcapManifestApp.Views
{
    public partial class CloudSetupPage : UserControl
    {
        public CloudSetupPage() => InitializeComponent();

        private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is CloudSetupViewModel vm)
                await vm.RefreshStatusCommand.ExecuteAsync(null);
        }
    }
}
