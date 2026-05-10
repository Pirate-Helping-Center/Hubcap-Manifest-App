using System.Windows.Controls;
using HubcapManifestApp.ViewModels;

namespace HubcapManifestApp.Views
{
    public partial class CloudDashboardPage : UserControl
    {
        public CloudDashboardPage() => InitializeComponent();

        private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is CloudDashboardViewModel vm)
                await vm.RefreshCommand.ExecuteAsync(null);
        }
    }
}
