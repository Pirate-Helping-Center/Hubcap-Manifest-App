using System.Windows.Controls;
using HubcapManifestApp.ViewModels;

namespace HubcapManifestApp.Views
{
    public partial class CloudPinningPage : UserControl
    {
        public CloudPinningPage() => InitializeComponent();

        private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is CloudPinningViewModel vm && vm.Apps.Count == 0)
                await vm.RefreshCommand.ExecuteAsync(null);
        }
    }
}
