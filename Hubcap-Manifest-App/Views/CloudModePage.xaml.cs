using System.Windows.Controls;
using System.Windows.Input;
using HubcapManifestApp.ViewModels;

namespace HubcapManifestApp.Views
{
    public partial class CloudModePage : UserControl
    {
        public CloudModePage() => InitializeComponent();

        private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is CloudModeViewModel vm)
                vm.RefreshCommand.Execute(null);
        }

        private void StFixerCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is CloudModeViewModel vm)
                vm.SelectStFixerCommand.Execute(null);
        }

        private void CloudRedirectCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is CloudModeViewModel vm)
                vm.SelectCloudRedirectCommand.Execute(null);
        }
    }
}
