using System.Windows.Controls;
using HubcapManifestApp.ViewModels;

namespace HubcapManifestApp.Views
{
    public partial class CloudExperimentalPage : UserControl
    {
        public CloudExperimentalPage() => InitializeComponent();

        private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is CloudExperimentalViewModel vm)
                vm.RefreshCommand.Execute(null);
        }
    }
}
