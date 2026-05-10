using System.Windows.Controls;
using HubcapManifestApp.ViewModels;

namespace HubcapManifestApp.Views
{
    public partial class HomePage : UserControl
    {
        public HomePage() => InitializeComponent();

        private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is HomeViewModel vm)
                await vm.LoadDashboardCommand.ExecuteAsync(null);
        }
    }
}
